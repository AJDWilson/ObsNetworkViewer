@echo off
rem Allows OBS Network Viewer to receive status broadcasts from other computers (run as administrator).
netsh advfirewall firewall delete rule name="OBS Network Viewer" >nul 2>&1
netsh advfirewall firewall add rule name="OBS Network Viewer" dir=in action=allow protocol=UDP localport=50505 profile=private,domain
pause
