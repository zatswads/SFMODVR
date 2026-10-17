@echo off
rem SF Avatar Import: pick a character (VaM .var / preset / zip, DAZ .duf / zip, FBX) and get an avatar for SF Body Morphs.
start "" powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "%~dp0import.ps1"
