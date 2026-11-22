# 从零把项目发布到 GitHub

> 面向第一次使用 GitHub 的人。按顺序做即可，全程不需要命令行基础也能完成。
> 仓库目录：`leadjoy\`（已包含 `README.md`、`LICENSE`、`docs\`、`src\` 和 `.gitignore`）。

---

## 0. 先确认：哪些文件不会上传

`.gitignore` 已经排除了以下内容，**不要**强行添加：

| 排除项 | 原因 |
| --- | --- |
| `tools/`、`third_party/` | 内置 .NET SDK、逆向工具、Binput 原始对照源码，体积大 |
| `dist/`、`build/`、`backups/`、`analysis/` | 发布包、构建产物、旧版本备份 |
| `settings.json`、`profiles/`、`save/`、`logs/` | 你的个人配置与设备信息 |
| `HANDOFF*.md` | 开发交接记录，含本机路径 |
| `docs/ff14/` | 第三方游戏截图，版权不属于本项目 |

上传前再扫一眼：仓库里不应出现你的用户名、蓝牙地址、游戏账号、路径。

---

## 1. 注册 GitHub 账号

1. 打开 <https://github.com/signup>；
2. 填写邮箱、密码、用户名（用户名会出现在仓库地址里，例如 `yure5`）；
3. 完成邮箱验证码；
4. 在邮箱里点击验证链接。

> 国内访问 GitHub 偶尔不稳定，可以换网络（手机热点）、改 DNS（`223.5.5.5` / `1.1.1.1`），或使用 GitHub Desktop 自带的登录。

---

## 2. 创建空仓库

1. 登录后点右上角 **+ → New repository**；
2. 填写：
   - **Repository name**：`leadjoy-blade-assistant`（或你喜欢的名字）；
   - **Description**：`Leadjoy 风刃 Plus 手柄映射 / 幻想模式 / 系统菜单 / 手柄输入法（Windows）`；
   - 选择 **Public**；
   - **不要**勾选 “Add a README file”“Add .gitignore”“Choose a license”——本地已经有；
3. 点 **Create repository**，记下页面上的仓库地址，例如：

```
https://github.com/你的用户名/leadjoy-blade-assistant.git
```

---

## 3. 安装 Git 客户端（二选一）

### 方案 A：GitHub Desktop（推荐新手）

1. 下载 <https://desktop.github.com/> 并安装；
2. 打开后 **Sign in to GitHub.com**，浏览器授权；
3. 菜单 **File → Add local repository**，选择 `leadjoy` 文件夹；
4. 如果提示 “not a Git repository”，点 **create a repository**，名称随意，路径保持 `leadjoy`；
5. 左下角填写提交信息（例如 `chore: 开源初始版本 V0.8.81`），点 **Commit to main**；
6. 点顶部 **Publish repository**，取消勾选 “Keep this code private”，选择你的账号，发布。

### 方案 B：命令行

如果电脑上已有 Git：

```powershell
cd <你的路径>\leadjoy
git init -b main
git config user.name "你的名字"
git config user.email "你的邮箱"
git add -A
git commit -m "chore: 开源初始版本 V0.8.81"
git remote add origin https://github.com/你的用户名/leadjoy-blade-assistant.git
git push -u origin main
```

推送时会要求登录。GitHub 不接受账号密码，需要用 **Personal Access Token**：

1. GitHub 右上角头像 → **Settings** → **Developer settings** → **Personal access tokens → Tokens (classic)**；
2. **Generate new token (classic)**，勾选 `repo` 权限，有效期选 30 天；
3. 复制生成的令牌，在推送提示密码时**粘贴令牌**（不是账号密码）。

> 本项目目录里自带了一个便携版 Git（`tools\git\cmd\git.exe`），但它被 `.gitignore` 排除，只用于本地准备；日常使用建议安装正式版 Git。

---

## 4. 发布 Release（提供下载）

GitHub 仓库本身不适合放 80 MB 的 exe，用 **Releases** 提供下载：

1. 进入仓库页面 → 右侧 **Releases → Create a new release**；
2. **Choose a tag** 输入 `v0.8.81`，点 **Create new tag**；
3. **Release title**：`V0.8.81`;
4. 描述可以直接粘贴 `CHANGELOG.md` 里 V0.8.81 一段，再补一句安装说明；
5. 把 `dist\风刃手柄助手-V0.8.81-win-x64.zip` 拖进 **Attach binaries**；
6. 点 **Publish release**。

README 里的 `[Releases](../../releases/latest)` 会自动指向最新版。

> 首次发布建议同时上传 `LICENSE` 与 `THIRD-PARTY-NOTICES.md` 的副本到 Release 附件，方便只下载压缩包的用户看到许可。

---

## 5. 完善仓库信息

进入仓库 **Settings**：

| 项目 | 建议 |
| --- | --- |
| Description | `Leadjoy 风刃 Plus 手柄映射、幻想模式、系统菜单与手柄输入法` |
| Topics | `leadjoy` `blade-plus` `gamepad` `controller-mapping` `wpf` `csharp` `windows` |
| Social preview | 上传 `docs\images\fantasy.png` 或自制封面 |
| Features → Issues | 开启 |
| Features → Discussions | 可选 |

---

## 6. 之后的日常更新

```powershell
git add -A
git commit -m "feat: 说明本次改动"
git push
```

发新版本时：

1. 修改 `CHANGELOG.md`、`AboutInfo.cs`、`LeadjoyBlade.App.csproj` 里的版本号；
2. 重新 publish；
3. 打 tag：`git tag v0.8.81 && git push origin v0.8.81`；
4. 在 Releases 里基于新 tag 创建发布并上传新 zip。

---

## 7. 中国大陆访问优化（可选）

- Release 下载慢：可在发布说明里额外提供网盘链接，或使用可信的 GitHub 加速镜像；
- 仓库图片加载慢：README 使用相对路径引用 `docs/images/`，图片随仓库加载，不走第三方图床；
- 不要把令牌写进脚本、README 或截图里。

---

## 8. 检查清单

- [ ] 仓库是 Public，README 能正常显示图片与目录；
- [ ] `LICENSE` 被 GitHub 识别为 GPL-3.0（仓库首页右侧显示）；
- [ ] Release 里有 `风刃手柄助手-V0.8.81-win-x64.zip`；
- [ ] 仓库里没有 `settings.json`、`profiles/`、`logs/`、`HANDOFF*.md`；
- [ ] README 里的下载链接、文档链接都能点开；
- [ ] 在手机浏览器上打开一次 README，确认排版正常。
