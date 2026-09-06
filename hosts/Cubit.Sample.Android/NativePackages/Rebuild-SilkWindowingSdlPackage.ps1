[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$sourceVersion = '2.23.1-cubit.1'
$targetVersion = '2.23.1-cubit.3'
$packageId = 'Silk.NET.Windowing.Sdl'
$androidAarPath = 'lib/net7.0-android33.0/Silk.NET.Windowing.Sdl.aar'
$sourcePackagePath = Join-Path $PSScriptRoot "$packageId.$sourceVersion.nupkg"
$targetPackagePath = Join-Path $PSScriptRoot "$packageId.$targetVersion.nupkg"
$workspaceRoot = [System.IO.DirectoryInfo]$PSScriptRoot
for ($index = 0; $index -lt 3; $index++) {
    $workspaceRoot = $workspaceRoot.Parent
}

$javaSourcePath = Join-Path $workspaceRoot.FullName '.cubit-probes\sdl-2.32.10-16k\android-project\app\src\main\java'
$androidSdkRoot = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
$androidJarPath = Join-Path $androidSdkRoot 'platforms\android-35\android.jar'
$javaHome = if ($env:JAVA_HOME) { $env:JAVA_HOME } else { 'C:\Program Files\Microsoft\jdk-25.0.3.9-hotspot' }
$javacPath = Join-Path $javaHome 'bin\javac.exe'
$jarPath = Join-Path $javaHome 'bin\jar.exe'

foreach ($path in @($sourcePackagePath, $javaSourcePath, $androidJarPath, $javacPath, $jarPath)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "缺少重建输入: $path"
    }
}

if (Test-Path -LiteralPath $targetPackagePath) {
    throw "目标包已存在，拒绝覆盖: $targetPackagePath"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$workDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("cubit-sdl-package-" + [Guid]::NewGuid().ToString('N'))
$classesDirectory = Join-Path $workDirectory 'classes'
$rebuiltClassesJarPath = Join-Path $workDirectory 'classes.jar'
$rebuiltAarPath = Join-Path $workDirectory 'Silk.NET.Windowing.Sdl.aar'
$rebuiltPackagePath = Join-Path $workDirectory "$packageId.$targetVersion.nupkg"

try {
    New-Item -ItemType Directory -Path $classesDirectory | Out-Null
    $javaSources = Get-ChildItem -Path $javaSourcePath -Filter '*.java' -Recurse | ForEach-Object FullName
    & $javacPath -source 8 -target 8 -Xlint:-options -nowarn -classpath $androidJarPath -d $classesDirectory $javaSources
    if ($LASTEXITCODE -ne 0) {
        throw "SDL Java 源码编译失败，exit=$LASTEXITCODE"
    }

    $sourcePackage = [System.IO.Compression.ZipFile]::OpenRead($sourcePackagePath)
    try {
        $sourceAarEntry = $sourcePackage.GetEntry($androidAarPath)
        if ($null -eq $sourceAarEntry) {
            throw "源包不含 Android AAR: $androidAarPath"
        }

        $sourceAarBytes = New-Object System.IO.MemoryStream
        try {
            $sourceAarStream = $sourceAarEntry.Open()
            try {
                $sourceAarStream.CopyTo($sourceAarBytes)
            }
            finally {
                $sourceAarStream.Dispose()
            }
            $sourceAarBytes.Position = 0
            $sourceAar = New-Object System.IO.Compression.ZipArchive($sourceAarBytes, [System.IO.Compression.ZipArchiveMode]::Read, $true)
            try {
                $sourceClassesEntry = $sourceAar.GetEntry('classes.jar')
                if ($null -eq $sourceClassesEntry) {
                    throw '源 AAR 不含 classes.jar'
                }

                $sourceClassesBytes = New-Object System.IO.MemoryStream
                try {
                    $sourceClassesStream = $sourceClassesEntry.Open()
                    try {
                        $sourceClassesStream.CopyTo($sourceClassesBytes)
                    }
                    finally {
                        $sourceClassesStream.Dispose()
                    }
                    $sourceClassesBytes.Position = 0
                    $sourceClassesJar = New-Object System.IO.Compression.ZipArchive($sourceClassesBytes, [System.IO.Compression.ZipArchiveMode]::Read, $true)
                    try {
                        # 保留 Silk 绑定程序集已生成映射的两个旧 Java 类型，其余 SDL 类全部来自 2.32.10 源码。
                        foreach ($entryName in @('org/libsdl/app/BuildConfig.class', 'org/libsdl/app/HIDDeviceBLESteamController$4.class')) {
                            $entry = $sourceClassesJar.GetEntry($entryName)
                            if ($null -eq $entry) {
                                throw "源 classes.jar 不含兼容类型: $entryName"
                            }
                            $destinationPath = Join-Path $classesDirectory $entryName
                            New-Item -ItemType Directory -Path (Split-Path $destinationPath -Parent) -Force | Out-Null
                            $input = $entry.Open()
                            $output = [System.IO.File]::Create($destinationPath)
                            try {
                                $input.CopyTo($output)
                            }
                            finally {
                                $output.Dispose()
                                $input.Dispose()
                            }
                        }
                    }
                    finally {
                        $sourceClassesJar.Dispose()
                    }
                }
                finally {
                    $sourceClassesBytes.Dispose()
                }

                & $jarPath --create --file $rebuiltClassesJarPath -C $classesDirectory .
                if ($LASTEXITCODE -ne 0) {
                    throw "SDL Java classes.jar 打包失败，exit=$LASTEXITCODE"
                }

                $rebuiltAar = [System.IO.Compression.ZipFile]::Open($rebuiltAarPath, [System.IO.Compression.ZipArchiveMode]::Create)
                try {
                    foreach ($entry in $sourceAar.Entries) {
                        $replacement = $rebuiltAar.CreateEntry($entry.FullName, [System.IO.Compression.CompressionLevel]::Optimal)
                        $replacement.LastWriteTime = $entry.LastWriteTime
                        $output = $replacement.Open()
                        try {
                            if ($entry.FullName -eq 'classes.jar') {
                                $input = [System.IO.File]::OpenRead($rebuiltClassesJarPath)
                            }
                            else {
                                $input = $entry.Open()
                            }
                            try {
                                $input.CopyTo($output)
                            }
                            finally {
                                $input.Dispose()
                            }
                        }
                        finally {
                            $output.Dispose()
                        }
                    }
                }
                finally {
                    $rebuiltAar.Dispose()
                }
            }
            finally {
                $sourceAar.Dispose()
            }
        }
        finally {
            $sourceAarBytes.Dispose()
        }

        $aarHash = (Get-FileHash -LiteralPath $rebuiltAarPath -Algorithm SHA256).Hash
        $provenance = @"
# Cubit Android SDL 16 KB 修订包

Package: ``$packageId`` ``$targetVersion``

本地包基于官方 ``Silk.NET.Windowing.Sdl`` ``2.23.0`` 包。除
``lib/net7.0-android33.0/Silk.NET.Windowing.Sdl.aar`` 外，其余托管 DLL、XML、依赖、许可证与非 Android 资产均保持不变。

该 AAR 保留原有四 ABI 原生库，尤其是经 16 KB 对齐审计的 ARM64 ``libSDL2.so`` 和 ``libmain.so``。只将 ``classes.jar`` 重建为 SDL ``2.32.10`` 的完整 ``org.libsdl.app`` Java 源；为保持 Silk 绑定的历史类型映射，额外保留原 jar 的 ``BuildConfig`` 与未使用的 HID 内部类。

- Official upstream nupkg SHA-256: ``FD6E4417F99E64EC6569818404C90E54E45857CD9B412F98CCA01DC97EA59E14``
- Replacement AAR SHA-256: ``$aarHash``
- ARM64 libSDL2.so SHA-256: ``FE6A6A91CFEA9E4938EE440A2FE9DCA5D51D4F7BEEC8EA5EEDEF37AAAB3F1CC9``
- Java source: ``.cubit-probes/sdl-2.32.10-16k/android-project/app/src/main/java``
- Java build contract: Android API 35 ``android.jar``, Java 8 bytecode

``Cubit.Tool androidpackagecheck`` 解析 ``SDLActivity.class`` 的版本常量并绑定上述 ARM64 SHA-256，防止 Java/native 版本再次漂移。
"@

        $rebuiltPackage = [System.IO.Compression.ZipFile]::Open($rebuiltPackagePath, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($entry in $sourcePackage.Entries) {
                $replacement = $rebuiltPackage.CreateEntry($entry.FullName, [System.IO.Compression.CompressionLevel]::Optimal)
                $replacement.LastWriteTime = $entry.LastWriteTime
                $output = $replacement.Open()
                try {
                    if ($entry.FullName -eq $androidAarPath) {
                        $input = [System.IO.File]::OpenRead($rebuiltAarPath)
                        try { $input.CopyTo($output) } finally { $input.Dispose() }
                    }
                    elseif ($entry.FullName -eq 'CUBIT-ANDROID-16KB.md') {
                        $writer = New-Object System.IO.StreamWriter($output, [System.Text.UTF8Encoding]::new($false), 1024, $true)
                        try { $writer.Write($provenance) } finally { $writer.Dispose() }
                    }
                    elseif ($entry.FullName -eq "$packageId.nuspec" -or $entry.FullName.EndsWith('.psmdcp')) {
                        $reader = New-Object System.IO.StreamReader($entry.Open())
                        try { $text = $reader.ReadToEnd().Replace($sourceVersion, $targetVersion) } finally { $reader.Dispose() }
                        $writer = New-Object System.IO.StreamWriter($output, [System.Text.UTF8Encoding]::new($false), 1024, $true)
                        try { $writer.Write($text) } finally { $writer.Dispose() }
                    }
                    else {
                        $input = $entry.Open()
                        try { $input.CopyTo($output) } finally { $input.Dispose() }
                    }
                }
                finally {
                    $output.Dispose()
                }
            }
        }
        finally {
            $rebuiltPackage.Dispose()
        }
    }
    finally {
        $sourcePackage.Dispose()
    }

    Move-Item -LiteralPath $rebuiltPackagePath -Destination $targetPackagePath
    Write-Host "已生成 $targetPackagePath"
    Write-Host "AAR SHA-256: $aarHash"
}
finally {
    if (Test-Path -LiteralPath $workDirectory) {
        Remove-Item -LiteralPath $workDirectory -Recurse -Force
    }
}
