$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$srcDir = Join-Path $root 'src'
$outDir = Join-Path $root 'bin'

$pshomeDir = $PSHOME
Add-Type -Path (Join-Path $pshomeDir 'Microsoft.CodeAnalysis.dll')
Add-Type -Path (Join-Path $pshomeDir 'Microsoft.CodeAnalysis.CSharp.dll')

# 只挑「含托管元数据」的程序集：运行时目录里混着大量原生 dll
function Test-Managed([string]$path) {
    try {
        $fs = [IO.File]::OpenRead($path)
        try {
            $br = New-Object IO.BinaryReader($fs)
            $fs.Position = 0x3C
            $peOff = $br.ReadInt32()
            $fs.Position = $peOff
            if ($br.ReadUInt32() -ne 0x00004550) { return $false }
            $fs.Position = $peOff + 24
            $magic = $br.ReadUInt16()
            $dataDirOff = if ($magic -eq 0x20B) { $peOff + 24 + 112 } else { $peOff + 24 + 96 }
            $fs.Position = $dataDirOff + 14 * 8
            return ($br.ReadUInt32() -ne 0)
        } finally { $fs.Dispose() }
    } catch { return $false }
}

# 选运行时版本：优先 .NET 10（与 Roslyn 同代），退到 8
function Pick-Version([string]$family) {
    $base = "C:\Program Files\dotnet\shared\$family"
    if (-not (Test-Path $base)) { return $null }
    $vers = Get-ChildItem $base -Directory | Sort-Object { [version]($_.Name -replace '-.*$','') } -Descending
    foreach ($v in $vers) { if ($v.Name -like '10.*') { return $v.Name } }
    return $vers[0].Name
}
$ver = Pick-Version 'Microsoft.WindowsDesktop.App'
if (-not $ver) { throw "找不到 WindowsDesktop 运行时" }
$coreDir = "C:\Program Files\dotnet\shared\Microsoft.NETCore.App\$ver"
$winDir  = "C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\$ver"
Write-Host "目标运行时: $ver"

$refs = @()
foreach ($d in @($coreDir, $winDir)) {
    $refs += Get-ChildItem $d -Filter *.dll | Where-Object { Test-Managed $_.FullName } |
             Select-Object -ExpandProperty FullName
}
Write-Host "托管参考程序集: $($refs.Count)"

$srcFiles = Get-ChildItem $srcDir -Filter *.cs | Sort-Object Name
Write-Host ("源文件: " + (($srcFiles | ForEach-Object { $_.Name }) -join ', '))

$trees = [System.Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
foreach ($f in $srcFiles) {
    $code = [IO.File]::ReadAllText($f.FullName, [Text.Encoding]::UTF8)
    $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($code))
}
$refList = [System.Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach ($r in $refs) { $refList.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($r)) }

$asmName = '性能监测'
$opt = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new(
    [Microsoft.CodeAnalysis.OutputKind]::WindowsApplication)
$opt = $opt.WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release)
$comp = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create($asmName, $trees, $refList, $opt)

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$outDll = Join-Path $outDir "$asmName.dll"

$sw = [Diagnostics.Stopwatch]::StartNew()
$fs = [IO.File]::Create($outDll)
$res = $comp.Emit($fs)
$fs.Close()
$sw.Stop()

$errs = @($res.Diagnostics | Where-Object { $_.Severity -eq [Microsoft.CodeAnalysis.DiagnosticSeverity]::Error })
$warns = @($res.Diagnostics | Where-Object { $_.Severity -eq [Microsoft.CodeAnalysis.DiagnosticSeverity]::Warning })
Write-Host ("编译 {0:N2}s  成功={1}  错误={2}  警告={3}" -f $sw.Elapsed.TotalSeconds, $res.Success, $errs.Count, $warns.Count)
$errs | Select-Object -First 25 | ForEach-Object { Write-Host ("  ERR " + $_.ToString()) }
if (-not $res.Success) { exit 1 }

# runtimeconfig：让 dll 能被 dotnet 启动
$rc = (@{
  runtimeOptions = @{
    tfm = "net$($ver.Split('.')[0]).0-windows"
    framework = @{ name = "Microsoft.WindowsDesktop.App"; version = $ver }
    configProperties = @{ "System.GC.Server" = $false; "System.GC.Concurrent" = $false }
  }
} | ConvertTo-Json -Depth 8)
$rcPath = Join-Path $outDir "$asmName.runtimeconfig.json"
[IO.File]::WriteAllText($rcPath, $rc, (New-Object Text.UTF8Encoding($false)))

# deps.json：没有它，宿主不会去 WindowsDesktop 框架里解析 WinForms 程序集，
# 进程会以 0xC0000142（DLL 初始化失败）直接退出。SDK 平时会自动生成这个文件。
$major = $ver.Split('.')[0]
$deps = (@{
  runtimeTarget = @{ name = ".NETCoreApp,Version=v$major.0"; signature = "" }
  compilationOptions = @{}
  targets = @{
    ".NETCoreApp,Version=v$major.0" = @{
      "$asmName/1.0.0" = @{
        runtime = @{}
        dependencies = @{
          "Microsoft.WindowsDesktop.App.WindowsForms" = "$major.0.0"
        }
      }
    }
  }
  libraries = @{
    "$asmName/1.0.0" = @{ type = "project"; serviceable = $false; sha512 = "" }
    "Microsoft.WindowsDesktop.App.WindowsForms/$major.0.0" = @{ type = "package"; serviceable = $false; sha512 = "" }
  }
} | ConvertTo-Json -Depth 10)
[IO.File]::WriteAllText((Join-Path $outDir "$asmName.deps.json"), $deps, (New-Object Text.UTF8Encoding($false)))

# 启动器：双击即可运行（无控制台窗口）
$launcher = @"
@echo off
start "" "C:\Program Files\dotnet\dotnet.exe" "%~dp0$asmName.dll"
"@
[IO.File]::WriteAllText((Join-Path $outDir '启动性能监测.cmd'), $launcher, [Text.Encoding]::GetEncoding(936))

Write-Host ("产出: {0} ({1:N0} 字节)" -f $outDll, (Get-Item $outDll).Length)
Write-Host ("启动器: " + (Join-Path $outDir '启动性能监测.cmd'))
