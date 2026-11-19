XRPL File for Windows

Run "XRPL File.exe". Nothing is installed.

Windows may stop the first start with "Windows protected your PC", because the
file is not code-signed yet. Click "More info", then "Run anyway".

Keep the "app" folder next to "XRPL File.exe": the application lives there.

Your key vault, publications and settings are stored in
%APPDATA%\XrplFile
unless you moved the vault elsewhere in the application. The vault holds your
keys: back it up, and export the keys you want to keep.

To remove the application, delete this folder, then these:
%APPDATA%\XrplFile
%LOCALAPPDATA%\XrplFile
%LOCALAPPDATA%\User Name\com.companyname.xrplfile.app
Deleting this folder alone leaves your vault, keys included, on the disk.

More: https://xrpl-file.staticbit.io
