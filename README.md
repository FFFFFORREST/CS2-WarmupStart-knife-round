# WarmupStartVote v1.2.0 — 无限热身 + 开赛投票 + 刀战选边 + .drop 发刀

适用：AWS Lightsail / Ubuntu 24.04、CS2 Dedicated Server、CounterStrikeSharp v1.0.376（.NET 10）。**与 RockTheVote-CSS 独立，不占用其 `!rtv`、`!nominate`、`!votemap`、`!maps` 命令。**

> 这是需要在你的服务器上编译的**源码部署包**，不是已测试过的 DLL。已进行脚本语法检查与配置检查；尚未在你的实际 CS2 服务端上完成编译和多人对局测试。先在没有重要比赛时部署、测试，出现编译错误保留完整输出。

## 一、预期流程

1. 新地图加载 → 默认进入**无限热身**：任何位置免费买枪、热身死亡复活。
2. 任意玩家输入 `!votestart`，开始 25 秒开赛投票。
3. 玩家输入 `!yes` / `!no`；赞成达到 `ceil(当前在线真人 × 50%)`，立即结束投票。
4. **双方各至少一名真人玩家**：服务器执行 `knife.cfg`，重新开始一局**只有刀、不能买枪、不能复活**的刀战。刀战输赢由 CS2 的 `round_end` 事件判断（CT 或 T）。
5. 刀战胜方成员任选一人输入 `!stay`（保持当前 CT/T）或 `!swap`（CT/T 交换）；获胜队先输入的有效选择生效。其他玩家不能替获胜队决定。
6. 选边后执行 `live.cfg`，**比分归零、恢复 $800 初始经济、正常买枪规则，MR12 正式比赛开始**。
7. 刀战结束后如果获胜队 30 秒内没有选边，默认 `!stay`，自动开赛，避免卡住。
8. 正式比赛结束后，插件尝试恢复无限热身；RockTheVote 切换新地图后也会恢复无限热身。
9. 投票、刀战、选边或正式比赛进行中，如果真人玩家全部退出，插件会自动回到无限热身，不会卡在比赛状态。

**单人测试例外：** 若在线仅一名真人，或者所有真人都处在同一队，无法进行有胜负的刀战。此时 `!votestart` + `!yes` 会**跳过刀战直接执行 `live.cfg`**，保持原先允许单人测试的规则。需要至少 T、CT 双方各一名真人，才能测试刀战与选边。插件不会自动填充机器人。

**特殊场景：** 刀战平局会重开刀战。刀战结束到选边期间进入暂停热身状态，以防自动开始第二局刀战。

## 二、玩家命令

| 命令 | 用途 | 谁能用 |
|---|---|---|
| `!votestart` | 发起开赛投票 | 所有玩家，至少 1 人 |
| `!yes` / `!startyes` | 赞成开赛 | 所有玩家 |
| `!no` / `!startno` | 反对开赛 | 所有玩家 |
| `!stay` | 刀战胜方保持原阵营 | 刀战胜方任一成员 |
| `!swap` | 刀战胜方与败方交换 CT/T | 刀战胜方任一成员 |
| `!startstatus` | 当前状态和开票情况 | 所有玩家 |
| `.drop` | 在自己脚下掉 5 把自己的刀，给其他玩家捡 | 活着且在 T/CT 的玩家；刀战中不可用。正式比赛每回合限一次，其他时候冷却 30 秒 |
| `!startreset` | 中止投票/刀战/比赛，强制回到无限热身 | 拥有 `@css/generic` 权限的管理员，或服务器控制台 / RCON（`css_startreset`） |

**选边是聊天命令，不是 F1/F2 或图形按钮。** `.drop` 用的是点号开头，直接在聊天框输入 `.drop`，全体聊天和队伍聊天都可以。 RockTheVote 的地图投票功能仍由原插件处理。

## 三、安装 / 从 v1.0 升级

你如果还没安装上一版 `WarmupStartVote.zip`，直接安装本版即可。如果已安装 v1.0 / v1.1 / v1.1.1，`install.sh` 会在**编译成功后**停止 CS2、替换旧 DLL，备份 `server.cfg`、`practice.cfg`、`live.cfg`、`knife.cfg` 和旧插件 DLL，然后重启。部署中途出错时脚本会自动把 CS2 重新拉起来，不会让服务器一直停着。

### 1. 将本 ZIP 上传到 Lightsail

用 Windows PowerShell（**电脑本地**）运行，替换路径和 IP：

```powershell
scp -i "C:\path\to\LightsailDefaultKey-us-east-1.pem" "C:\path\to\WarmupStartVote_v1.2.0_knife.zip" ubuntu@你的静态IP:/home/ubuntu/
```

或者用 WinSCP/SFTP 把 ZIP 传到 `/home/ubuntu/`。

### 2. 服务器上的操作（Lightsail SSH，ubuntu 用户）

```bash
sudo apt update
sudo apt install -y unzip dotnet-sdk-10.0
```

```bash
cd /home/ubuntu
```

```bash
unzip -o WarmupStartVote_v1.2.0_knife.zip
```

```bash
cd /home/ubuntu/WarmupStartVote
```

```bash
bash install.sh
```

安装脚本会从当前 CounterStrikeSharp 目录提取 API DLL 用于编译，成功后才会部署。编译需要对应的 .NET 10 SDK。

注意：如已安装旧版，本包解压会覆盖 `/home/ubuntu/WarmupStartVote/` 的旧**源码**，并在编译成功后更新服务器中的插件 DLL；RockTheVote-CSS 文件不动。

### 3. 检查状态

```bash
sudo systemctl status cs2 --no-pager
```

```bash
sudo journalctl -u cs2 -n 150 --no-pager | grep -Ei 'WarmupStartVote|Warmup Start Vote|error|exception'
```

```bash
sudo ls -lh /home/steam/cs2/game/csgo/addons/counterstrikesharp/plugins/WarmupStartVote/WarmupStartVote.dll
```

## 四、实际测试

### 单人

- 进入服务器、确认热身与免费买枪。
- 聊天：`!votestart`，再输入 `!yes`。
- 预期：出现“skipping knife round”提示，马上进入正式配置。这是预期行为，不是刀战失效。

### 两人以上（至少一 CT、一 T）

- 两人在不同阵营加入，执行 `!votestart`，再投 `!yes`。
- 预期：进入刀战。枪械被移除，只保留刀；正常只打一个回合。
- 刀战赢家所在队的一名玩家输入 `!stay` / `!swap`。
- 如果 `!swap`，两队阵营交换（场上的 Bot 也一起换边）；然后比赛重启，比分从 0:0 开始，经济恢复正常。
- 也可不选边，等待 30 秒，默认留边正式开赛。

**切图测试：** RockTheVote-CSS 成功切换到官方地图后，应重新进入无限练习。

## 五、插件源码里可调整的参数

`source/WarmupStartVote.cs`：

- `MinimumPlayers = 1` — 最少允许开票人数
- `ApprovalFraction = 0.50` — 50% 赞成通过
- `StartVoteSeconds = 25f` — 开赛投票倒计时
- `StartVoteCooldownSeconds = 30` — 投票失败后，再次发起投票的冷却时间
- `SidePickSeconds = 30f` — 胜方选边时限，超时默认留边
- `ResetPermission = "@css/generic"` — 使用 `!startreset` 所需的管理员权限
- `DropKnifeCount = 5` — 每次 `.drop` 掉几把刀
- `DropCooldownSeconds = 30` — 热身、投票、选边阶段 `.drop` 的冷却时间（正式比赛中改为每回合一次）

修改后再运行 `bash install.sh` 重新编译部署。

`configs/`：

- `practice.cfg` — 无限热身、随地免费购买
- `knife.cfg` — 刀局规则、只给刀、禁止购买和炸弹
- `live.cfg` — 选边后 MR12、比分重置、恢复经济

不建议在 `server.cfg` 额外写 `exec knife.cfg` 或 `exec live.cfg`；插件会自动调用。

## 六、备份及回退

安装前配置备份会放在 `/home/ubuntu/cs2-startvote-backup-日期时间/`。如需回退：

```bash
sudo systemctl stop cs2
```

```bash
sudo mv /home/steam/cs2/game/csgo/addons/counterstrikesharp/plugins/WarmupStartVote /home/ubuntu/WarmupStartVote-disabled
```

从备份目录恢复原有 `server.cfg`，并按需恢复 `practice.cfg`、`live.cfg`、`knife.cfg`（要退回旧版插件，就把备份里的 `WarmupStartVote.dll` 放回插件目录），再执行：

```bash
sudo systemctl start cs2
```

## 七、注意事项

- 这是**轻量自定义插件**，不是 MatchZy 那种完整比赛管理系统；不提供自动队伍配置、加时赛、暂停和录像管理。
- 当前由 CS2 原生 `round_end` 判断刀战胜队，并在胜方选边后用 `SwitchTeam()` 交换双方；服务器版本更新后建议回归测试。
- 投票比例按**在线真人玩家数**计算，不把 Bot 算进投票人数。刀战也要求真人玩家分布在两个阵营。
- 一名获胜队员输入的第一个有效选边指令即生效，不要求获胜队内部二次投票。
- 不要将敏感 GSLT Token、服务器管理凭据放进压缩包或发在截图里。

## 八、v1.1.1 更新内容（修复）

- **修复编译错误**：源码缺少 `using CounterStrikeSharp.API.Modules.Utils;`，`CsTeam` 无法识别，v1.1 无法编译。
- **刀战开局判定**：`knife.cfg` 里的 `mp_warmup_end` / `mp_restartgame` 会额外触发回合开始 / 结束事件，以前可能被误判为“平局”或刀战已经开始。现在改为冻结时间结束（`round_freeze_end`）且不在热身中时才算刀战开始，并忽略重启产生的 `round_end`（原因 GameCommencing）。
- **`!swap` 带上 Bot**：以前只交换真人，场上的 Bot 留在原队，会出现两队混在一起的情况。
- **玩家全部离开时自动复位**：以前比赛中所有人退出后，插件会一直停在 LIVE 状态，新玩家无法 `!votestart`。
- **新增管理员命令 `!startreset`**：出现异常状态时可手动回到无限热身。
- **投票中有人退出会重新计票**：可能直接通过或直接失败，不用干等倒计时结束。
- **投票冷却改为从投票失败时开始计算**：以前从发起时算，25 秒的投票超时失败后只需再等 5 秒。
- **`!startstatus` 票数**：不再计入已离开玩家的票。
- **cfg**：刀战 / 正式比赛补上 `mp_roundtime`、`mp_roundtime_hostage`（人质图以前回合时长不对）；`practice.cfg` 恢复死亡掉枪 / 掉雷，以及默认近战武器。
- **install.sh**：部署中途失败时自动重启 CS2；额外备份旧插件 DLL；修正 .NET SDK 检测在 `pipefail` 下可能误报的问题。

## 九、v1.2.0 新功能：.drop 发刀

- 聊天输入 `.drop`：服务器给你发 5 把刀，从你脚下上方竖直落地，排成一个小圈，其他玩家可以去捡。你自己手里的刀不受影响。
- 发的是 `weapon_knife`。CS2 通常会换成发起者库存里装备的那把刀（带皮肤），所以别人捡到的是你的刀。**这一点取决于 CS2 本身，请实测确认**；如果捡到的只是默认刀，说明当前版本的 CS2 不会这样替换。
- 捡刀：玩家手里已经有刀时，需要对着地上的刀按 E 交换，或者先按 G 丢掉自己的刀再走过去。为此 `practice.cfg` 和 `live.cfg` 开启了 `mp_drop_knife_enable 1`，`knife.cfg` 中关闭。
- 限制：必须活着并在 T/CT；刀战（等待开始和进行中）不可用；正式比赛每回合每人一次；热身、投票、选边阶段每人冷却 30 秒（热身是一个不会结束的回合，按回合限制会变成整个热身只能用一次）。
