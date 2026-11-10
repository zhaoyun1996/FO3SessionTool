@echo off
REM Run as Administrator BEFORE starting the launcher. Captures fifazf.exe traffic only.
cd /d "%~dp0"
if exist game_capture_redacted.txt del game_capture_redacted.txt
"%APPDATA%\Python\Python315\Scripts\mitmdump.exe" --mode local:fifazf.exe -s game_capture.py --set connection_strategy=lazy --ssl-insecure
pause
