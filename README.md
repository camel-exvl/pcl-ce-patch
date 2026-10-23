# PCL CE Restore

第三方基于 PCL-CE 官方稳定 Release 制作的补丁版，恢复 Minecraft 安装、下载、整合包、修复和启动补全，并持续跟踪后续稳定版本。

PCL 原作者为 [龙腾猫跃](https://afdian.com/a/LTCat)，PCL-CE 由 [PCL Community](https://github.com/PCL-Community/PCL-CE) 继续开发。本项目由第三方独立维护。原有作品的许可和署名见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

## 下载与更新

从本仓库 [Releases](https://github.com/camel-exvl/pcl-ce-patch/releases) 下载`PCL_CE_Restore_x64.exe`，关闭旧启动器后放入原有启动器目录使用。需要与官方版相同的 .NET 10 Desktop Runtime。

补丁版的程序内更新只从本仓库 `updates` 分支读取更新信息。稳定和 Beta 设置均跟踪稳定补丁版；不会回退到官方原版。更新源不可达时，现有程序继续使用，更新检查报告失败。首次运行仍会显示与官方版相同的初始化、购买和赞助提示。

版本形如 `2.15.1-restored.1`。同一上游版本修订补丁时递增最后一位，因此可以通过程序内更新获得修订。ZIP 是程序内更新所需的完整更新包；清单中的 SHA-256 对应解压后的 exe。

## 实现与发布流程

1. 每六小时和手动触发时检查官方最新稳定 Release；修改工具、适配规则或工作流并推送 `main` 也会触发检查。
2. 下载官方 x64 exe 及 Sigstore 证明，同时验证官方资产 SHA-256 和上游工作流身份。
3. 使用原 exe 作为 .NET Bundle 模板，只修改主程序集内四个方法及版本元数据。`PCL.Core.dll` 和其他捆绑文件逐字节保留。没有提取、记录或重新注入微软登录、CurseForge 等服务凭据。
4. 在 Windows x64 运行集成验证和 WPF 启动检查。
5. 全部验证通过后，为补丁 exe、更新 ZIP 和更新清单包生成本仓库的 Sigstore 证明，发布完整 Release，再通过单次 Git 分支更新切换更新源。

发布中断后重新运行工作流：草稿资产可以重试；已发布资产保持不变，从已有 `update-feed.zip` 恢复更新源发布。不会将更新源指向尚未发布完成的文件。发布报告包含原始/补丁摘要、四个修改方法和全部保留文件的摘要。

仅适配本机使用的 x64 Windows，不处理 ARM64 或 32 位 x86。当前适配基线为官方 `v2.15.1`（包含 #3673、#3676）。适配规则记录限制入口、调用方、更新器及其编译器生成方法的规范化 IL 摘要。上游改动这些方法时，即使补丁仍可写入也会停止发布，等待审查和适配。该检查覆盖已识别的实现，新增且完全独立的限制仍需人工审查；不保证任何未来上游版本都无需维护。

`Assess()` 及地区检测库保留。`IsRestricted` 返回 false，`Check` 返回 true，`ThrowIfRestricted` 直接返回。更新源初始化只替换源列表，并保留后续 `serverLoader` 等初始化。版本更新、下载、解压、校验及重启替换继续使用原有实现。

## 启用自动同步

目标仓库已配置为 `camel-exvl/pcl-ce-patch`，工作流位于 `.github/workflows/release.yml`。

1. 在 GitHub Actions 中启用工作流。仓库需允许 Actions 使用 `contents: write` 和 `id-token: write`；这两项只授予发布任务。无需设置官方服务密钥、PAT 或 MirrorChyan 配置。
2. 在 **Sync and patch official release → Run workflow** 运行一次。`upstream_tag` 留空跟踪最新稳定版，或填写 `v2.15.1`。
3. 首版完成后，在真实使用环境验收微软登录、正版游戏启动、CurseForge 下载和整合包安装。CI 不持有用户账号或游戏资源，无法代替这些交互验收。

仅支持 Intel／AMD x64 Windows，使用 `windows-latest` runner。GitHub 可能在公开仓库 60 天无活动后停用定时工作流；在 Actions 中重新启用或手动运行即可恢复。

迁移仓库时先修改 `patch-config.json` 中的 `repository`，递增 `patchRevision` 后重新构建。已安装旧补丁版仍指向旧仓库，因此首次迁移需手动下载新仓库的 exe。

## 本地构建与验证

需要 .NET 10 SDK、Python 3.10+。Windows 上可使用当前目录的 `.tools/dotnet/dotnet.exe`；将 SDK 解压到该目录即可。下面的环境脚本会将 SDK 主目录、NuGet、Python、pip 和临时目录指向本项目 `.tools/`，并关闭首次启动证书生成与全局 PATH 修改。

```powershell
. ./scripts/local-env.ps1
dotnet restore tests/PclCeRestore.Tests/PclCeRestore.Tests.csproj --locked-mode
dotnet build src/PclCeRestore -c Release --no-restore
python -m unittest discover -s tests -p 'test_*.py' -v
```

将已验证的官方文件放到 `artifacts/originals/`，执行：

```powershell
dotnet run --project src/PclCeRestore -c Release --no-restore -- patch `
  --input artifacts/originals/PCL2_CE_Release_x64.exe `
  --output artifacts/patched/x64/PCL_CE_Restore_x64.exe `
  --feed-base https://raw.githubusercontent.com/camel-exvl/pcl-ce-patch/updates/ `
  --revision 1 --profile profiles/upstream-v2.15.1.json `
  --report artifacts/patched/x64/patch-report.json

dotnet run --project tests/PclCeRestore.Tests -c Release --no-restore -- `
  artifacts/originals/PCL2_CE_Release_x64.exe profiles/upstream-v2.15.1.json `
  .tools/tmp https://raw.githubusercontent.com/camel-exvl/pcl-ce-patch/updates/
```

单独验证使用 `verify --original EXE --patched EXE --feed-base URL --revision N --profile JSON`。验证失败的候选文件不会成为正式输出。集成测试实际执行恢复后的入口，验证重打包一致性，并验证核心库、其他方法、修订号或更新地址被改动时会拒绝产物。测试不会启动整个启动器或登录账号。`scripts/smoke.ps1` 会启动程序，默认只在 CI 中运行。

源码重编译无法继承官方 Actions 的服务配置，因此本项目始终使用官方成品作为输入。原有服务配置的有效性与相同官方版本一致；服务端验证方式变化时需要重新评估。

## 适配上游变化与回退

工作流报告 `Compatibility review required` 时，下载并验证新官方成品，使用 `inspect --input EXE --report artifacts/candidate-profile.json` 生成候选摘要，结合相应上游源码审查差异。确认入口与更新协议兼容后，更新工具及审核过的 profile，递增 `patchRevision`，运行全部验证后提交。不要在工作流中自动信任新 profile。

Release 附带 `upstream-source.tar.gz` 和 `source-equivalent.patch`，用于审查二进制修改对应的源码行为；源码差异不包含服务凭据。输入与输出的证明分开，补丁产物不再使用官方签名身份。

需要回退时关闭启动器，手动下载上一个补丁 Release 的 exe 替换，并在更新设置中关闭自动更新。自动流程拒绝将更新源降级；处理故障期间保留上一可用版和原有游戏目录。
