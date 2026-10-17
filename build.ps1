<#
    build.ps1 —— 编译 mod，并把 DLL 放到 Assemblies\

    ── 为什么不直接 dotnet build ──
    两件事必须由脚本代劳：

    1) 提交哈希
       csproj 开了 ContinuousIntegrationBuild，正常情况下 MSBuild 会从
       .git 里读出提交哈希、嵌进 AssemblyInformationalVersion。
       但本项目的 .git 在 mod 文件夹**外面**（见 git.ps1 的说明），
       MSBuild 从项目目录向上找找不到它，于是不再嵌入。
       这里显式用 /p:SourceRevisionId=<hash> 传进去。

       效果：加载日志会打印形如
           [RimForex] 0.2.0+1b5ce6e... loaded.
       看这行就知道游戏里装的是哪一版、由哪份源码编出来的。

    2) 清理构建产物
       bin\ 与 obj\ 虽然被 .gitignore 排除，但**物理上存在于 mod 文件夹里**。
       RimWorld 上传创意工坊时把整个 mod 根目录交给 Steam，不排除任何子目录，
       于是这些文件会被一起分发（实测 324 KB）。
       编译完顺手删掉。

    用法：
        .\build.ps1
#>

$ErrorActionPreference = "Stop"

$modDir = "C:\Users\12435\Desktop\test\RimForex"
$repoDir = "C:\Users\12435\Desktop\test\RimForex.git"
$srcDir = Join-Path $modDir "Source"

if (-not (Test-Path $repoDir)) { throw "找不到仓库：$repoDir" }
if (-not (Test-Path $srcDir)) { throw "找不到源码目录：$srcDir" }

# ── 取提交哈希 ──
$sha = & git --git-dir=$repoDir --work-tree=$modDir rev-parse HEAD 2>$null
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sha)) {
    Write-Warning "读不到提交哈希，本次构建不会嵌入源码版本"
    $sha = ""
}
else {
    Write-Host ("提交: {0}" -f $sha.Substring(0, 7))
}

# ── 编译 ──
Push-Location $srcDir
try {
    $buildArgs = @("build", "RimForex.csproj", "-v", "minimal", "-nologo")
    if ($sha) { $buildArgs += "/p:SourceRevisionId=$sha" }

    & dotnet @buildArgs
    $code = $LASTEXITCODE
}
finally {
    Pop-Location
}

if ($code -ne 0) {
    Write-Error "编译失败（退出码 $code）"
    exit $code
}

# ── 清理构建产物（不留在 mod 文件夹里，否则会被传到创意工坊）──
foreach ($p in @("bin", "obj")) {
    $full = Join-Path $srcDir $p
    if (Test-Path $full) {
        $n = (Get-ChildItem $full -Recurse -File -Force -ErrorAction SilentlyContinue |
              Measure-Object Length -Sum)
        Remove-Item $full -Recurse -Force
        Write-Host ("已清理 Source\{0}（{1} 个文件，{2:N0} KB）" -f $p, $n.Count, ($n.Sum / 1KB))
    }
}

# ── 结果 ──
$dll = Join-Path $modDir "Assemblies\RimForex.dll"
if (Test-Path $dll) {
    $h = (Get-FileHash $dll -Algorithm SHA256).Hash.Substring(0, 16)
    Write-Host ("DLL: {0}  {1:N0} B" -f $h, (Get-Item $dll).Length)
}
else {
    Write-Error "没有产出 Assemblies\RimForex.dll"
    exit 1
}
