# Builds bin\Isle.exe with the Roslyn compiler from Visual Studio Build Tools.
# Targets .NET Framework 4.8 (built into Windows), so nothing extra needs installing.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fx = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$csc = & $vswhere -latest -products * -find "MSBuild\**\Bin\Roslyn\csc.exe" | Select-Object -First 1
if (-not $csc) { throw "Roslyn csc.exe not found. Install 'Visual Studio Build Tools'." }

# WinRT metadata from the Windows SDK: the "Windows" facade plus the two API contracts it forwards to
$kits = "${env:ProgramFiles(x86)}\Windows Kits\10"
$sdk = Get-ChildItem "$kits\References" -Directory | Where-Object Name -match '^10\.' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $sdk) { throw "Windows 10/11 SDK not found under $kits\References." }
$winmdRefs = @("$kits\UnionMetadata\$($sdk.Name)\Facade\windows.winmd") + (
    @('Windows.Foundation.FoundationContract', 'Windows.Foundation.UniversalApiContract') | ForEach-Object {
        Get-ChildItem "$($sdk.FullName)\$_\*\$_.winmd" | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    })

$out = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $out | Out-Null

$ico = Join-Path $root 'assets\isle.ico'
if (-not (Test-Path $ico)) { & (Join-Path $root 'tools\make-icon.ps1') -Out $ico | Out-Null }

$refs = @(
    "$fx\WPF\PresentationCore.dll", "$fx\WPF\PresentationFramework.dll", "$fx\WPF\WindowsBase.dll",
    "$fx\System.Xaml.dll", "$fx\System.Xml.dll", "$fx\System.dll", "$fx\System.Core.dll", "$fx\System.Drawing.dll",
    "$fx\System.Windows.Forms.dll", "$fx\Microsoft.CSharp.dll", "$fx\System.Management.dll",
    "$fx\WPF\UIAutomationClient.dll", "$fx\WPF\UIAutomationTypes.dll",
    "$fx\System.Runtime.dll", "$fx\System.Runtime.WindowsRuntime.dll",
    "$fx\System.Runtime.InteropServices.WindowsRuntime.dll", "$fx\System.Threading.Tasks.dll",
    "$fx\System.IO.dll", "$fx\System.ObjectModel.dll", "$fx\System.Web.Extensions.dll"
) + $winmdRefs

$cscArgs = @(
    '/nologo', '/noconfig', '/target:winexe', '/optimize+', '/langversion:latest', '/platform:anycpu',
    '/nowarn:1701,1702', "/out:$out\Isle.exe",
    "/win32manifest:$root\app.manifest", "/win32icon:$ico",
    "/resource:$root\src\Island.xaml,Isle.Island.xaml", "/resource:$root\src\Hub.xaml,Isle.Hub.xaml", "/resource:$ico,Isle.isle.ico"
) + (Get-ChildItem "$root\browser\*" -File | ForEach-Object { "/resource:$($_.FullName),Isle.browser.$($_.Name)" }) + ($refs | ForEach-Object { "/reference:$_" }) + (Get-ChildItem "$root\src\*.cs" | ForEach-Object FullName)

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed." }
Copy-Item "$root\app.config" "$out\Isle.exe.config" -Force
"Built: $out\Isle.exe"

# ---- Isle for Android (phone app), if the Android SDK is on this PC ----
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
if (Test-Path "$env:LOCALAPPDATA\Android\Sdk\platforms\android-35\android.jar") {
    # a missing JDK (Android Studio removed) shouldn't stop the Windows build: the last built APK is kept
    try { & (Join-Path $root 'android\build-apk.ps1') | Select-Object -Last 1 }
    catch { Write-Warning "Phone app not rebuilt ($($_.Exception.Message)); using the existing dist\Isle-Phone.apk." }
}
$apk = Join-Path $dist 'Isle-Phone.apk'

# ---- single-file installer (Isle.exe travels inside it, like an .apk) ----
$setupRefs = @(
    "$fx\WPF\PresentationCore.dll", "$fx\WPF\PresentationFramework.dll", "$fx\WPF\WindowsBase.dll",
    "$fx\System.Xaml.dll", "$fx\System.Xml.dll", "$fx\System.dll", "$fx\System.Core.dll", "$fx\Microsoft.CSharp.dll"
)
$setupArgs = @(
    '/nologo', '/noconfig', '/target:winexe', '/optimize+', '/langversion:latest', '/platform:anycpu',
    '/nowarn:1701,1702', "/out:$dist\IsleSetup.exe",
    "/win32manifest:$root\app.manifest", "/win32icon:$ico",
    "/resource:$out\Isle.exe,payload.Isle.exe", "/resource:$out\Isle.exe.config,payload.Isle.exe.config",
    "/resource:$ico,setup.isle.ico", "/resource:$root\setup\Setup.xaml,setup.Setup.xaml"
) + ($(if (Test-Path $apk) { @("/resource:$apk,payload.Isle-Phone.apk") } else { @() })) + ($setupRefs | ForEach-Object { "/reference:$_" }) + "$root\setup\Setup.cs"

& $csc @setupArgs
if ($LASTEXITCODE -ne 0) { throw "Installer build failed." }
"Built: $dist\IsleSetup.exe"
