// a2f-bake: offline lip-sync curves for SF Body Morphs.
// Runs NVIDIA Audio2Face-3D (SDK + TensorRT engines, built locally, see README.md) over every WAV under a folder and writes one CSV per
// clip: t (seconds) then the 52 ARKit skin weights and 16 tongue weights of the blendshape solve, one row per frame. The plugin plays
// these curves when the clip plays and maps the mouth weights onto her face bones.
//
// Input WAVs can be any rate / channel count / PCM 8-32 or float: they are downmixed to mono and resampled to 16 kHz (windowed sinc).
// Clips are batched over the executor's tracks; each track is reset between clips. Existing CSVs newer than their WAV are skipped.

#include "audio2face/audio2face.h"
#include "audio2x/cuda_utils.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <memory>
#include <mutex>
#include <sstream>
#include <string>
#include <vector>

namespace fs = std::filesystem;

// Pose order of the solve's output: bs_skin.npz poseNames[1..] then bs_tongue.npz poseNames[1..] (Claire v2.3.1 and v3.0; checked
// 2026-10-16). The tool refuses a model whose weight count differs.
static const char* kSkin[] = {
    "eyeBlinkLeft", "eyeLookDownLeft", "eyeLookInLeft", "eyeLookOutLeft", "eyeLookUpLeft", "eyeSquintLeft", "eyeWideLeft",
    "eyeBlinkRight", "eyeLookDownRight", "eyeLookInRight", "eyeLookOutRight", "eyeLookUpRight", "eyeSquintRight", "eyeWideRight",
    "jawForward", "jawLeft", "jawRight", "jawOpen", "mouthClose", "mouthFunnel", "mouthPucker", "mouthLeft", "mouthRight",
    "mouthSmileLeft", "mouthSmileRight", "mouthFrownLeft", "mouthFrownRight", "mouthDimpleLeft", "mouthDimpleRight",
    "mouthStretchLeft", "mouthStretchRight", "mouthRollLower", "mouthRollUpper", "mouthShrugLower", "mouthShrugUpper",
    "mouthPressLeft", "mouthPressRight", "mouthLowerDownLeft", "mouthLowerDownRight", "mouthUpperUpLeft", "mouthUpperUpRight",
    "browDownLeft", "browDownRight", "browInnerUp", "browOuterUpLeft", "browOuterUpRight", "cheekPuff", "cheekSquintLeft",
    "cheekSquintRight", "noseSneerLeft", "noseSneerRight", "tongueOut"};
static const char* kTongue[] = {
    "tongueTipUp", "tongueTipDown", "tongueTipLeft", "tongueTipRight", "tongueRollUp", "tongueRollDown", "tongueRollLeft",
    "tongueRollRight", "tongueUp", "tongueDown", "tongueLeft", "tongueRight", "tongueIn", "tongueStretch", "tongueWide",
    "tongueNarrow"};
static const int kNSkin = 52, kNTongue = 16, kNW = kNSkin + kNTongue, kJawOpen = 17;

struct Destroyer { template <typename T> void operator()(T* o) const { if (o) o->Destroy(); } };
template <typename T> using UPtr = std::unique_ptr<T, Destroyer>;

#define CHECK(expr)                                                                                  \
    do {                                                                                             \
        std::error_code ec_ = (expr);                                                                \
        if (ec_) { std::cerr << "error: " << #expr << ": " << ec_.message() << std::endl; std::exit(1); } \
    } while (0)

// ---------------- WAV ----------------

static uint32_t U32(const uint8_t* p) { return p[0] | (p[1] << 8) | (p[2] << 16) | ((uint32_t)p[3] << 24); }
static uint16_t U16(const uint8_t* p) { return (uint16_t)(p[0] | (p[1] << 8)); }

// Mono float samples at the file's rate; false on anything it can't read.
static bool ReadWav(const fs::path& path, std::vector<float>& out, int& rate, std::string& why)
{
    std::ifstream f(path, std::ios::binary);
    if (!f) { why = "can't open"; return false; }
    std::vector<uint8_t> b((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
    if (b.size() < 12 || std::memcmp(b.data(), "RIFF", 4) || std::memcmp(b.data() + 8, "WAVE", 4)) { why = "not RIFF/WAVE"; return false; }
    int fmt = 0, ch = 0, bits = 0; rate = 0;
    const uint8_t* data = nullptr; size_t dataLen = 0;
    for (size_t p = 12; p + 8 <= b.size();)
    {
        uint32_t len = U32(&b[p + 4]);
        const uint8_t* c = &b[p + 8];
        size_t avail = std::min<size_t>(len, b.size() - (p + 8));
        if (!std::memcmp(&b[p], "fmt ", 4) && avail >= 16)
        {
            fmt = U16(c); ch = U16(c + 2); rate = (int)U32(c + 4); bits = U16(c + 14);
            if (fmt == 0xFFFE && avail >= 26) fmt = U16(c + 24);   // extensible: the sub-format GUID starts with the format tag
        }
        else if (!std::memcmp(&b[p], "data", 4)) { data = c; dataLen = avail; }
        p += 8 + len + (len & 1);
    }
    if (!data || ch <= 0 || rate <= 0) { why = "no fmt/data chunk"; return false; }
    if (!((fmt == 1 && (bits == 8 || bits == 16 || bits == 24 || bits == 32)) || (fmt == 3 && (bits == 32 || bits == 64))))
    { why = "unsupported format " + std::to_string(fmt) + "/" + std::to_string(bits); return false; }
    int bps = bits / 8;
    size_t n = dataLen / (size_t)(bps * ch);
    out.assign(n, 0.f);
    for (size_t i = 0; i < n; i++)
    {
        double s = 0;
        for (int k = 0; k < ch; k++)
        {
            const uint8_t* q = data + (i * ch + k) * bps;
            double v;
            if (fmt == 3) { if (bits == 32) { float x; std::memcpy(&x, q, 4); v = x; } else { double x; std::memcpy(&x, q, 8); v = x; } }
            else if (bits == 8) v = (q[0] - 128) / 128.0;
            else if (bits == 16) v = (int16_t)U16(q) / 32768.0;
            else if (bits == 24) { int32_t x = (q[0] << 8) | (q[1] << 16) | (q[2] << 24); v = (x >> 8) / 8388608.0; }
            else v = (int32_t)U32(q) / 2147483648.0;
            s += v;
        }
        out[i] = (float)(s / ch);
    }
    return true;
}

// Windowed-sinc (Blackman) resampler to 16 kHz; the cutoff sits at 0.95 x the lower Nyquist.
static std::vector<float> To16k(const std::vector<float>& x, int rate)
{
    const int R = 16000;
    if (rate == R) return x;
    double ratio = (double)rate / R;
    double fc = 0.95 * std::min(1.0, 1.0 / ratio);           // in input-sample units
    int half = (int)std::ceil(16.0 / fc);
    size_t n = (size_t)std::floor(x.size() / ratio);
    std::vector<float> y(n);
    const double PI = 3.14159265358979323846;
    for (size_t i = 0; i < n; i++)
    {
        double t = i * ratio;
        long c = (long)std::floor(t);
        double acc = 0, wsum = 0;
        for (long k = c - half + 1; k <= c + half; k++)
        {
            if (k < 0 || k >= (long)x.size()) continue;
            double d = t - k;
            double s = std::abs(d) < 1e-9 ? 1.0 : std::sin(PI * fc * d) / (PI * fc * d);
            double u = (d + half) / (2.0 * half);
            double w = (u <= 0 || u >= 1) ? 0 : 0.42 - 0.5 * std::cos(2 * PI * u) + 0.08 * std::cos(4 * PI * u);
            acc += x[k] * s * w; wsum += s * w;
        }
        y[i] = (float)(wsum != 0 ? acc / wsum : 0);
    }
    return y;
}

// ---------------- bake ----------------

struct Clip
{
    fs::path wav, csv; std::string rel; std::vector<float> audio;
    std::vector<double> t; std::vector<float> w;   // frames x kNW
    std::mutex m; bool err = false;
};

struct Track { Clip* clip = nullptr; double rate = 16000; };

static void OnResults(void* user, const nva2f::IBlendshapeExecutor::HostResults& r, std::error_code ec)
{
    auto* tracks = static_cast<std::vector<Track>*>(user);
    Track& tr = (*tracks)[r.trackIndex];
    if (!tr.clip) return;
    std::lock_guard<std::mutex> lock(tr.clip->m);
    if (ec) { tr.clip->err = true; return; }
    tr.clip->t.push_back((double)r.timeStampCurrentFrame / tr.rate);
    const float* p = r.weights.Data();
    for (int k = 0; k < kNW; k++) tr.clip->w.push_back(k < (int)r.weights.Size() ? p[k] : 0.f);
}

static void Usage()
{
    std::cerr <<
        "usage: a2f-bake --model <model.json> --in <wav folder> --out <csv folder> [options]\n"
        "  --type diffusion|regression   model kind (default: diffusion if the path contains 'diffusion')\n"
        "  --identity N                  diffusion identity (0 = Claire, default)\n"
        "  --fps N                       regression frame rate (default 30; diffusion is always 60)\n"
        "  --emotion name=v,name=v       fixed emotion for every frame (names as the model lists them)\n"
        "  --tracks N                    clips run together (default 8)\n"
        "  --force                       rebake even when the CSV is newer than the WAV\n";
}

int main(int argc, char** argv)
{
    std::string model, in, out, type, emo; int identity = 0, fps = 30, nTracks = 8; bool force = false;
    for (int i = 1; i < argc; i++)
    {
        std::string a = argv[i];
        auto next = [&]() -> std::string { if (i + 1 >= argc) { Usage(); std::exit(2); } return argv[++i]; };
        if (a == "--model") model = next(); else if (a == "--in") in = next(); else if (a == "--out") out = next();
        else if (a == "--type") type = next(); else if (a == "--identity") identity = std::stoi(next());
        else if (a == "--fps") fps = std::stoi(next()); else if (a == "--emotion") emo = next();
        else if (a == "--tracks") nTracks = std::max(1, std::stoi(next())); else if (a == "--force") force = true;
        else { Usage(); return 2; }
    }
    if (model.empty() || in.empty() || out.empty()) { Usage(); return 2; }
    bool diffusion = type.empty() ? model.find("diffusion") != std::string::npos : type == "diffusion";

    // the clips to do
    std::vector<std::unique_ptr<Clip>> clips;
    fs::path inRoot = fs::u8path(in), outRoot = fs::u8path(out);
    int skipped = 0;
    for (auto& e : fs::recursive_directory_iterator(inRoot))
    {
        if (!e.is_regular_file()) continue;
        std::string ext = e.path().extension().string();
        std::transform(ext.begin(), ext.end(), ext.begin(), ::tolower);
        if (ext != ".wav") continue;
        auto c = std::make_unique<Clip>();
        c->wav = e.path();
        fs::path rel = fs::relative(e.path(), inRoot);
        c->rel = rel.generic_u8string();
        c->csv = outRoot / rel; c->csv.replace_extension(".csv");
        if (!force && fs::exists(c->csv) && fs::last_write_time(c->csv) >= fs::last_write_time(c->wav)) { skipped++; continue; }
        clips.push_back(std::move(c));
    }
    std::cout << clips.size() << " clips to bake, " << skipped << " up to date" << std::endl;
    if (clips.empty()) return 0;

    CHECK(nva2x::SetCudaDeviceIfNeeded(0));
    nTracks = (int)std::min<size_t>(nTracks, clips.size());

    UPtr<nva2f::IBlendshapeExecutorBundle> bundle;
    std::vector<std::string> emoNames; std::vector<float> emoVec;
    if (diffusion)
    {
        nva2f::IDiffusionModel::IGeometryModelInfo* mi = nullptr;
        bundle.reset(nva2f::ReadDiffusionBlendshapeSolveExecutorBundle(nTracks, model.c_str(),
            nva2f::IGeometryExecutor::ExecutionOption::All, false, identity, true, &mi, nullptr));
        if (mi)
        {
            auto& ni = mi->GetNetworkInfo();
            for (size_t k = 0; k < ni.GetEmotionsCount(); k++) emoNames.push_back(ni.GetEmotionName(k));
            if (identity < (int)ni.GetIdentityLength()) std::cout << "identity " << ni.GetIdentityName(identity) << std::endl;
            mi->Destroy();
        }
    }
    else
    {
        nva2f::IRegressionModel::IGeometryModelInfo* mi = nullptr;
        bundle.reset(nva2f::ReadRegressionBlendshapeSolveExecutorBundle(nTracks, model.c_str(),
            nva2f::IGeometryExecutor::ExecutionOption::All, false, fps, 1, &mi, nullptr));
        if (mi)
        {
            auto& ni = mi->GetNetworkInfo();
            for (size_t k = 0; k < ni.GetEmotionsCount(); k++) emoNames.push_back(ni.GetEmotionName(k));
            std::cout << "identity " << ni.GetIdentityName() << std::endl;
            mi->Destroy();
        }
    }
    if (!bundle) { std::cerr << "error: can't load " << model << std::endl; return 1; }
    auto& ex = bundle->GetExecutor();
    if (ex.GetResultType() != nva2f::IBlendshapeExecutor::ResultsType::HOST) { std::cerr << "error: expected host results (CPU solver)" << std::endl; return 1; }
    if ((int)ex.GetWeightCount() != kNW) { std::cerr << "error: model gives " << ex.GetWeightCount() << " weights, expected " << kNW << std::endl; return 1; }
    size_t fn = 0, fd = 1; ex.GetFrameRate(fn, fd);
    std::cout << (diffusion ? "diffusion" : "regression") << ", " << (double)fn / fd << " fps, emotions:";
    for (auto& n : emoNames) std::cout << ' ' << n;
    std::cout << std::endl;

    // fixed emotion
    emoVec.assign(bundle->GetEmotionAccumulator(0).GetEmotionSize(), 0.f);
    if (!emo.empty())
    {
        std::stringstream ss(emo); std::string kv;
        while (std::getline(ss, kv, ','))
        {
            auto eq = kv.find('=');
            std::string n = kv.substr(0, eq); float v = eq == std::string::npos ? 1.f : std::stof(kv.substr(eq + 1));
            auto it = std::find(emoNames.begin(), emoNames.end(), n);
            if (it == emoNames.end() || (size_t)(it - emoNames.begin()) >= emoVec.size()) { std::cerr << "error: no emotion '" << n << "'" << std::endl; return 2; }
            emoVec[it - emoNames.begin()] = v;
        }
    }

    std::vector<Track> tracks(nTracks);
    for (auto& t : tracks) t.rate = (double)ex.GetSamplingRate();
    CHECK(ex.SetResultsCallback(OnResults, &tracks));
    cudaStream_t stream = bundle->GetCudaStream().Data();

    fs::create_directories(outRoot);
    std::ofstream index(outRoot / "curves.txt", std::ios::app);
    auto t0 = std::chrono::steady_clock::now();
    int done = 0, failed = 0; double audioSec = 0;
    for (size_t base = 0; base < clips.size(); base += nTracks)
    {
        // load and feed this batch
        for (int k = 0; k < nTracks; k++)
        {
            tracks[k].clip = nullptr;
            CHECK(ex.Reset(k));
            CHECK(bundle->GetAudioAccumulator(k).Reset());
            CHECK(bundle->GetEmotionAccumulator(k).Reset());
            if (base + k >= clips.size()) continue;
            Clip& c = *clips[base + k];
            std::vector<float> raw; int rate = 0; std::string why;
            if (!ReadWav(c.wav, raw, rate, why)) { std::cerr << "skip " << c.rel << ": " << why << std::endl; failed++; continue; }
            c.audio = To16k(raw, rate);
            if (c.audio.size() < 1600) { std::cerr << "skip " << c.rel << ": shorter than 0.1 s" << std::endl; failed++; continue; }
            audioSec += c.audio.size() / 16000.0;
            tracks[k].clip = &c;
            CHECK(bundle->GetAudioAccumulator(k).Accumulate(nva2x::HostTensorFloatConstView{c.audio.data(), c.audio.size()}, stream));
            CHECK(bundle->GetAudioAccumulator(k).Close());
            CHECK(bundle->GetEmotionAccumulator(k).Accumulate(0, nva2x::HostTensorFloatConstView{emoVec.data(), emoVec.size()}, stream));
            CHECK(bundle->GetEmotionAccumulator(k).Close());
        }
        while (nva2x::GetNbReadyTracks(ex) > 0) CHECK(ex.Execute(nullptr));
        for (int k = 0; k < nTracks; k++) if (tracks[k].clip) CHECK(ex.Wait(k));

        // write
        for (int k = 0; k < nTracks; k++)
        {
            Clip* c = tracks[k].clip;
            if (!c) continue;
            tracks[k].clip = nullptr;
            size_t nf = c->t.size();
            if (c->err || nf == 0) { std::cerr << "fail " << c->rel << (c->err ? ": solver error" : ": no frames") << std::endl; failed++; continue; }
            // frames can arrive out of order from the solver's threads: sort by time
            std::vector<size_t> ord(nf); for (size_t i = 0; i < nf; i++) ord[i] = i;
            std::sort(ord.begin(), ord.end(), [&](size_t a, size_t b) { return c->t[a] < c->t[b]; });
            fs::create_directories(c->csv.parent_path());
            std::ofstream o(c->csv);
            o << "t";
            for (auto n : kSkin) o << ',' << n;
            for (auto n : kTongue) o << ',' << n;
            o << '\n';
            char buf[32]; float peakJaw = 0;
            for (size_t i : ord)
            {
                std::snprintf(buf, sizeof buf, "%.4f", c->t[i]); o << buf;
                for (int w = 0; w < kNW; w++) { float v = c->w[i * kNW + w]; std::snprintf(buf, sizeof buf, ",%.4f", v); o << buf; }
                o << '\n';
                peakJaw = std::max(peakJaw, c->w[i * kNW + kJawOpen]);
            }
            index << c->rel << '\t' << c->audio.size() / 16000.0 << "\t" << nf << "\t" << peakJaw << '\n';
            c->audio.clear(); c->audio.shrink_to_fit(); c->t.clear(); c->w.clear();
            c->t.shrink_to_fit(); c->w.shrink_to_fit();
            done++;
        }
        std::cout << "\r" << done << "/" << clips.size() << " baked" << std::flush;
    }
    double sec = std::chrono::duration<double>(std::chrono::steady_clock::now() - t0).count();
    std::cout << "\n" << done << " baked, " << failed << " failed, " << audioSec << " s of audio in " << sec << " s" << std::endl;
    return failed ? 3 : 0;
}
