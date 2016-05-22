Requires Windows.  Only tested on Windows 10.

Building from source:
 - Download visual studio express for desktop
 - Try to build solution
 - If it complains about NuGet packages missing then right click on solution and click "Manage Nuget Packages" and then click Restore
 - Under Configuration Manager make sure both projects are set to build and are set to x86
 - Download and copy the dll's in the official EDSDK to the canon_edsdk sensor directory in DySense (ie move everything in /EDSDK/EDSDK/Dll to /DySense/sensors/camera/canon_edsdk)
 - If you make any changes you should just need to copy the DySenseCanonEDSDK.exe from /DySenseCanonEDSDK/bin/x86/Debug and overwrite the one already stored in DySense.