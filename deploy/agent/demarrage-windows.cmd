@echo off
rem MonitorKing - lancer l'agent a chaque ouverture de session, avec les droits administrateur (temperatures).
rem Windows demande une confirmation. Pour ne plus le lancer : demarrage-windows.cmd /desinstaller
if /i "%~1"=="/desinstaller" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0demarrage-windows.ps1" -Desinstaller
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0demarrage-windows.ps1"
)
