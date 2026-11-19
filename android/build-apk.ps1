# Builds dist\Isle-Phone.apk offline with the Android SDK + Android Studio's JDK (no Gradle, no downloads).
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$sdk = "$env:LOCALAPPDATA\Android\Sdk"
$bt = if (Test-Path "$sdk\build-tools\35.0.0") { "$sdk\build-tools\35.0.0" } else {
    (Get-ChildItem "$sdk\build-tools" | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1).FullName }
$jar = "$sdk\platforms\android-35\android.jar"
$jdk = "C:\Program Files\Android\Android Studio\jbr"
if (-not (Test-Path $jar)) { throw "Android SDK platform 35 not found." }
$env:JAVA_HOME = $jdk
$env:Path = "$jdk\bin;$env:Path"

$out = Join-Path $here 'build'
$dist = Join-Path (Split-Path $here) 'dist'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out, "$out\gen", "$out\classes", "$out\dex", $dist | Out-Null

function Run($exe) {
    $ErrorActionPreference = 'Continue'   # tools print notes on stderr; only the exit code decides
    & $exe @args 2>&1 | ForEach-Object { "$_" } | Where-Object { $_ -notmatch '^Note:|deprecat|^\s*$' }
    if ($LASTEXITCODE -ne 0) { throw "$([IO.Path]::GetFileName($exe)) failed ($LASTEXITCODE)" }
}

Run "$bt\aapt2.exe" compile --dir "$here\res" -o "$out\res.zip"
Run "$bt\aapt2.exe" link -o "$out\app.unsigned.apk" -I $jar --manifest "$here\AndroidManifest.xml" --java "$out\gen" `
    --min-sdk-version 29 --target-sdk-version 35 --version-code 4 --version-name 1.3 "$out\res.zip"

$sources = @(Get-ChildItem "$here\src", "$out\gen" -Recurse -Filter *.java | ForEach-Object FullName)
Run "$jdk\bin\javac.exe" -encoding UTF-8 --release 8 -nowarn -classpath $jar -d "$out\classes" @sources

$classes = @(Get-ChildItem "$out\classes" -Recurse -Filter *.class | ForEach-Object FullName)
Run "$bt\d8.bat" --release --min-api 29 --lib $jar --output "$out\dex" @classes

Push-Location "$out\dex"
try { Run "$bt\aapt.exe" add "$out\app.unsigned.apk" classes.dex | Out-Null } finally { Pop-Location }
Run "$bt\zipalign.exe" -p -f 4 "$out\app.unsigned.apk" "$out\app.aligned.apk"
Run "$bt\apksigner.bat" sign --ks "$env:USERPROFILE\.android\debug.keystore" --ks-pass pass:android --key-pass pass:android `
    --ks-key-alias androiddebugkey --out "$dist\Isle-Phone.apk" "$out\app.aligned.apk"
Run "$bt\apksigner.bat" verify "$dist\Isle-Phone.apk"
"Built: $dist\Isle-Phone.apk"
