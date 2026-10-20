# KeepingYouAwake for Windows

🌐 [English](README.md) · **日本語**

PC がスリープしないようにする、Windows のタスクトレイ常駐ユーティリティです。決めた時間のあいだ、またはトリガーが続く間（監視中のアプリが動いている、特定の Wi-Fi に接続している、ダウンロード中、アクティブ時間内 など）スリープを防ぎます。

Marcel Dierkes 氏の macOS アプリ [KeepingYouAwake](https://github.com/newmarcel/KeepingYouAwake) に Amphetamine 風の機能を加えた fork [KeepingYouAwake (Amphetamine)](https://github.com/ryouka0731/KeepingYouAwake-Amphetamine) を、C# / .NET で Windows 10 / 11 向けに書き直したものです。機能・設定の考え方・アクティビティログの形式はそのまま引き継いでいます。upstream のメンテナーの要望に従い、名前とアイコン（オレンジのカップ）は本家と区別しています。

## 機能

| | 機能 |
|---|---|
| ☕ | **トレイアイコンをクリック**でオン/オフ。右クリックで時間を選択（5 分〜5 時間、無期限、*今日の終わりまで*、独自の時間） |
| ⏱ | 残り時間をツールチップとメニューに表示。開始・終了時の通知（オフにも可） |
| 🖥 | ディスプレイも点けたまま、またはシステムだけ起こしてディスプレイの電源オフは許可 |
| 📶 | **監視 Wi-Fi** — 指定のネットワークに接続している間 |
| 🧩 | **監視アプリ** — `obs64.exe` や `Teams.exe` などが実行中の間 |
| ⬇️ | **ダウンロード** — 監視フォルダーに `.crdownload` / `.part` などがある間 |
| 🗓 | **アクティブ時間** — 曜日 × 時間帯（深夜 0 時をまたぐ設定も可） |
| 🔌 | **AC 電源** — 電源につないでいる間 |
| 🖵 | **外部ディスプレイ** — 接続している間 |
| 🔊 | **外部音声出力** — Bluetooth / USB / HDMI から音を出している間 |
| 📈 | **CPU 負荷** — CPU 使用率がしきい値を 30 秒以上上回っている間 |
| 🔋 | バッテリー: 残量が下回ったら・バッテリー節約機能がオンになったら・フル充電になったら停止 |
| 💽 | システムディスク / ほかのドライブのスピンダウン防止、マウスジグラー |
| 👥 | 別ユーザーに切り替えたら一時停止し、戻ったら再開 |
| 📜 | すべてのセッションのアクティビティログ（`%APPDATA%\KeepingYouAwake\activity.jsonl`） |
| 🔗 | `keepingyouawake:` URL スキーム、コマンドライン引数、Python CLI、MCP サーバー |
| 🌐 | 英語・日本語 |

監視項目にはそれぞれチェックボックスがあり、削除せずに一時停止できます。トリガーは「何も動いていないときにだけ開始し、自分が開始したセッションだけを終了する」ので、手動で始めたセッションをトリガーが止めることはありません。

## インストール

[Releases](https://github.com/ryouka0731/KeepingYouAwakeForWin/releases) から `KeepingYouAwake-<version>-win-x64.zip`（ARM 版 PC は `-win-arm64.zip`）をダウンロードし、好きな場所（例: `%LOCALAPPDATA%\Programs\KeepingYouAwake`）に展開して `KeepingYouAwake.exe` を起動します。単一の実行ファイルで、インストーラーも .NET ランタイムも不要です。各 zip には `.sha256` が付いています。

コード署名はしていないため、初回起動時に SmartScreen の警告が出ることがあります。**詳細情報 → 実行** を選んでください。

サインイン時に自動起動するには **設定 → 一般 → ログイン時に起動** をオンにします。カップのアイコンを隠れたアイコン領域からタスクバーへドラッグしておくと常に見えて便利です。

### ソースからビルド

```powershell
git clone https://github.com/ryouka0731/KeepingYouAwakeForWin.git
cd KeepingYouAwakeForWin
dotnet run --project src/KeepingYouAwake
# 単一ファイルとしてビルド:
dotnet publish src/KeepingYouAwake -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

.NET 10 SDK が必要です。構成は [CONTRIBUTING.md](CONTRIBUTING.md) を参照してください。

## 自動化

アプリは現在のユーザーに `keepingyouawake:` URL スキームを登録します。

| URL | 動作 |
|-----|------|
| `keepingyouawake:///activate` | デフォルトの時間で開始 |
| `keepingyouawake:///activate?seconds=90`（`minutes=` / `hours=` も可） | 指定時間だけ開始（`0` で無期限） |
| `keepingyouawake:///deactivate` | 停止 |
| `keepingyouawake:///toggle` | 切り替え |

```powershell
Start-Process "keepingyouawake:///activate?minutes=30"
# 実行ファイルに直接渡す場合:
KeepingYouAwake.exe --activate 1800   # 秒。省略するとデフォルトの時間
KeepingYouAwake.exe --deactivate
KeepingYouAwake.exe --toggle
```

- [`tools/cli`](tools/cli/README.md) — `kya activate 30m`、`kya status --json` など
- [`tools/mcp-server`](tools/mcp-server/README.md) — AI アシスタントからセッションを開始・停止・確認できる MCP サーバー

どちらも URL スキームでアプリを操作し、アクティビティログを読みます。

## 仕組み

セッション中は Windows の **電源要求**（`PowerCreateRequest` / `PowerSetRequest`。「システム必須」と、ディスプレイの電源オフを許可していなければ「ディスプレイ必須」）を保持します。`powercfg /requests` で確認できます。手動でスリープさせることはでき、ノート PC のふたを閉じたときの動作は Windows の設定に従います。

設定は `%APPDATA%\KeepingYouAwake\settings.json`、アクティビティログはその隣に保存されます。

## macOS 版との違い

- 監視アプリはバンドル ID ではなく実行ファイル名（`obs64.exe`）で判定します。
- Windows 11 24H2 以降では、位置情報へのアクセスを許可したアプリにしか Wi-Fi のネットワーク名が見えません。Wi-Fi トリガーが反応しないときは、［設定］›［プライバシーとセキュリティ］›［位置情報］で「デスクトップアプリが位置情報にアクセスできるようにする」をオンにしてください。
- 「低電力モード」は Windows のバッテリー節約機能（省電力モード）に対応します。
- Drive Alive はシステム以外のすべてのローカルドライブを対象にします（Windows では内蔵と外付けの固定ドライブを確実に区別できないため）。
- AppleScript / ショートカットの代わりに、URL スキーム・コマンドライン引数・CLI・MCP サーバーを使います。
- アップデートは GitHub Releases を確認してダウンロードページを開きます（自動で置き換えはしません）。

## ライセンス

MIT（upstream と同じ）。オリジナルの KeepingYouAwake は Marcel Dierkes 氏の著作物で、独立してメンテナンスされています。この移植版は同氏の著作権表示を残したうえで、移植版の著作権表示を追加しています。[LICENSE](LICENSE) を参照してください。
