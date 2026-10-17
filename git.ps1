<#
    git.ps1 —— 在 mod 文件夹里正常使用 git

    ── 为什么需要它 ──
    本项目的 git 仓库**不在 mod 文件夹里**，而在同级目录：

        test\
        ├── RimForex\          mod 文件夹（没有 .git）
        └── RimForex.git\      仓库本体

    原因是 RimWorld 上传 Steam 创意工坊时，会把 mod 根目录**整个**交给 Steam
    （ModMetaData.GetWorkshopUploadDirectory() 直接返回 RootDir，
     然后 SteamUGC.SetItemContent(handle, hook.Directory.FullName)），
    不排除任何子目录。若 .git 留在 mod 文件夹里，完整版本历史 ——
    包括所有历史源码与提交人邮箱 —— 会被一并分发给每个订阅者。

    ── 怎么用 ──
    在 mod 文件夹里，把平时写的 `git` 换成 `.\git.ps1`：

        .\git.ps1 status
        .\git.ps1 add -A
        .\git.ps1 commit -m "说明"
        .\git.ps1 push

    参数会原样转发给 git，所以任何子命令、任何选项都照常可用。

    ── 想直接敲 `git` 的话 ──
    把你的 PowerShell 配置文件（$PROFILE）里加一行：

        function git { & "C:\Users\12435\Desktop\test\RimForex\git.ps1" @args }

    这样在这个会话里 `git status` 就等于 `.\git.ps1 status`。
    （但会影响所有目录下的 git 命令，所以没替你写进去。）

    ── 为什么用 $args 而不是 param 块 ──
    最初这里写的是 [CmdletBinding()] +
    param([Parameter(ValueFromRemainingArguments = $true)][string[]] $GitArgs)。
    那会引入 PowerShell 的**通用参数**：-Verbose、-Debug、-ErrorAction 等。
    于是 `.\git.ps1 remote -v` 里的 -v 被当成 -Verbose 的缩写吃掉，
    传给 git 的只剩 `remote` —— 输出少了一半，而且**不报错**，极难发现。
    同类受影响的还有 -d / -e / -w / -o 等短选项。

    改用 $args 之后，PowerShell 不再解析任何参数名，全部原样转发。
#>

$ErrorActionPreference = "Stop"

$repoDir = "C:\Users\12435\Desktop\test\RimForex.git"
$workDir = "C:\Users\12435\Desktop\test\RimForex"

if (-not (Test-Path $repoDir)) {
    Write-Error "找不到仓库：$repoDir"
    exit 1
}
if (-not (Test-Path $workDir)) {
    Write-Error "找不到工作区：$workDir"
    exit 1
}

# git 用这两个环境变量定位仓库与工作区，等价于 --git-dir / --work-tree
$env:GIT_DIR       = $repoDir
$env:GIT_WORK_TREE = $workDir

$code = 0
try {
    & git @args
    $code = $LASTEXITCODE
}
finally {
    # 清掉，免得污染同一会话里其他仓库的 git 操作
    Remove-Item Env:\GIT_DIR       -ErrorAction SilentlyContinue
    Remove-Item Env:\GIT_WORK_TREE -ErrorAction SilentlyContinue
}

exit $code
