@echo off
REM Run as Administrator. Captures only 3seaLauncher.exe traffic.
cd /d "%~dp0"
if exist capture_redacted.txt del capture_redacted.txt
"%APPDATA%\Python\Python315\Scripts\mitmdump.exe" --mode local:3seaLauncher.exe -s redact_capture.py --set connection_strategy=lazy
pause
