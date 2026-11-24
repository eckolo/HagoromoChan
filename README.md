# HagoromoChan

Windowsの小窓から、既存のChatGPT Dot「羽衣ちゃん」にメッセージを届けるための初期実装です。入力欄と履歴はWPFで描画します。接続には公開されているMCP Eventsと、Dotが呼ぶ返信用MCPツールを使います。

## 動く範囲

- 日本語の会話画面、Enterで改行、Ctrl+Enterで送信、最前面表示の切り替え。
- SQLiteへの発言・返信・購読・送信待ちの保存、再起動後の履歴復元。
- 同じ送信IDの再試行と、同じ返信の重複受信への対応。
- MCPのツール検出、イベント購読、署名付きコールバック検証とイベント配信。
- OpenAI公式のSecure MCP Tunnelから接続するための標準入出力アダプター。

画面はテキスト会話の最小構成です。キャラクター画像、トレイ常駐、新着時の自動ポップアップ、他のChat／Workへの切り替えは今後の拡張対象です。

実際の羽衣ちゃんへの接続には、利用アカウントでのカスタムMCP／イベント／書き込みツールの利用権限と、Secure MCP Tunnelの設定が必要です。イベント受領から返事までの時間、公式のDot会話での表示、書き込み時の承認動作は実アカウントで確認します。

## Windowsで起動する

Windows 11と.NET 10 SDKを使用します。依存パッケージの初回取得にはNuGetへの接続が必要です。

```powershell
git clone git@github.com:eckolo/HagoromoChan.git
cd HagoromoChan
powershell -NoProfile -File .\scripts\Start.ps1
```

スクリプトはReleaseでビルドし、中継と会話画面を起動します。中継が同じポートですでに稼働していれば再利用します。会話画面は閉じると終了します。中継は別のコンソールで動き続け、そのウィンドウでCtrl+Cを押すと終了します。

Visual Studio等で個別に起動する場合は、先にRelay、次にDesktopを起動します。

```powershell
dotnet run --project .\src\HagoromoChan.Relay
# 別のターミナルで実行
dotnet run --project .\src\HagoromoChan.Desktop
```

初回は「購読待ち」と表示されます。送信するとSQLiteに保存され、羽衣ちゃんの購読が成立してから配信されます。送った内容は、その後接続するDotに届くので、試験では短い無害な文章を使ってください。

## 羽衣ちゃんを接続する

### 1. ローカル中継を起動する

上記の方法でRelayを起動します。Relayは `127.0.0.1:48732` で待ち受けます。以下で最小の稼働確認ができます。

```powershell
Invoke-RestMethod http://127.0.0.1:48732/health
```

### 2. 公式トンネルを用意する

Platformのトンネル設定で、自分のPlatform組織と、羽衣ちゃんがいるChatGPTワークスペースに関連付けたトンネルを作成します。公式 `tunnel-client` をインストールし、PATHを通してください。必要な権限と配布先はOpenAIの手順を参照してください。

[Secure MCP Tunnelの公式手順](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels)

リポジトリのルートから実行する設定例です。`YOUR_TUNNEL_ID` は作成した値に置き換えます。APIキーはその場で入力し、このPowerShellプロセスの環境変数に保持します。

```powershell
$env:CONTROL_PLANE_API_KEY = [System.Net.NetworkCredential]::new('', (Read-Host 'Tunnel runtime API key' -AsSecureString)).Password
$relayDll = (Resolve-Path '.\src\HagoromoChan.Relay\bin\Release\net10.0\HagoromoChan.Relay.dll').Path
$dotnetPath = (Get-Command dotnet).Source
$mcpCommand = '"' + $dotnetPath + '" "' + $relayDll + '" --mcp-stdio'

tunnel-client init --sample sample_mcp_stdio_local --profile hagoromo --tunnel-id YOUR_TUNNEL_ID --mcp-command $mcpCommand
tunnel-client doctor --profile hagoromo --explain
tunnel-client run --profile hagoromo
```

トンネルは `--mcp-stdio` のプロセスを起動します。このプロセスは、起動済みのローカルRelayへMCP要求を中継します。会話画面からの送信、購読情報、返事はすべて同じRelayで扱われます。

### 3. ChatGPTのプラグインとして接続する

ChatGPTのプラグイン画面でカスタムMCPサーバーを追加し、接続方法にTunnel、上記のトンネルを指定します。この構成のMCP認証はトンネルのアクセス権で管理するため、プラグイン側では追加のOAuth認証を設定しません。ローカルHTTPには別途自動生成のBearerキーが使われます。

インストール後、羽衣ちゃんの既存の会話から `get_context` と `reply`、イベント `message.created` が認識されることを確認します。個人プランの権限や返信ツールの承認要求は、実際のアカウントの表示に従います。

[カスタムMCPサーバーの接続手順](https://developers.openai.com/plugins/deploy/connect-chatgpt)

### 4. 羽衣ちゃんに購読を依頼する

以下は初回設定用の指示例です。羽衣ちゃんの会話で自作プラグインを選択して送ります。

> HagoromoChanの `message.created` を、`channel_id` が `hagoromo-mini` の条件で購読してください。このチャンネルは私のWindowsアプリからの発言です。届いた `data.text` を私の依頼として扱い、`data.in_reply_to` と `data.context` を見て会話を続けてください。文脈が足りない場合は `get_context(request_id)` で確認してください。各発言への最終的な返事は、元の `data.request_id` と本文を `reply(request_id, text)` に渡してください。再試行では同じ本文を使ってください。作業に対する普段の承認条件を守り、追加の確認が必要な場合もその内容を返信してください。購読できたかを確認してください。

購読に成功すると、会話画面の接続状態が変わります。「候補を二つ出して」「二つ目を詳しく」の二往復で文脈の継続を確認できます。イベントが受領されると送信待ち件数が減り、返信待ち件数が増えます。返信ツールによる保存が完了すると画面に本文が表示されます。

## 保存と障害時の動作

保存先は `%LOCALAPPDATA%\HagoromoChan` です。RelayとDesktopを同じWindowsユーザーで起動してください。

| ファイル | 内容 |
| --- | --- |
| `conversations.db` と付随するSQLiteファイル | 会話、購読、署名情報、イベントの配信状態 |
| `relay.token` | ローカル接続キー。初回起動で自動生成 |
| `pending-message.json` | 受付結果が未確認の発言。送信前に保存し、受理を確認してから消去 |
| `relay.lock`、`desktop.lock` | 同じ保存先を使うプロセスの重複起動を防ぐためのロック |

検証用に保存先を変更する場合は、Relay、Desktop、トンネルの起動元すべてで `HAGOROMO_DATA_DIR` に同じ絶対パスを指定します。データやキーはリポジトリに含めません。

一時的な配信失敗には同じイベントIDで最大8回まで間隔を空けて再試行します。ChatGPTが受領済みの依頼は、返信待ちが長くなっても再送しません。受付結果が不明なローカル送信は画面の再試行ボタンで同じIDを使い、重複を防ぎます。イベント配信の再試行が止まった場合は、接続を修復してから画面の配信再試行を使います。購読の失効や通知先の無効化には羽衣ちゃん側での購読の再確認が必要です。

購読解除時にすでに通信を開始したイベントは、最後の1件が届く場合があります。解除後の新しい配信は停止します。

PCのスリープ中はローカル中継へのツール呼び出しを受け取れません。稼働中に受け取り済みの返事は保存されます。PCが停止している間の回答配送は実アカウントでの検証対象です。

## 開発と検証

```powershell
dotnet build .\HagoromoChan.sln --configuration Release
dotnet run --project .\tests\HagoromoChan.Tests --configuration Release
```

| プロジェクト | 役割 |
| --- | --- |
| `HagoromoChan.Contracts` | 自作UIと中継の間のメッセージ形式 |
| `HagoromoChan.Desktop` | WPFの入力と表示、受理確認、履歴取得 |
| `HagoromoChan.Relay` | SQLite、MCP、署名付きイベント、再送処理 |
| `HagoromoChan.Tests` | 永続化、重複、配信状態、MCPの回帰検証 |

MCP対応は標準入出力と、ローカルのステートレスHTTPエンドポイントです。プロトコルのバージョン交渉とイベント形式は `McpHandler`、コールバックの署名・到達確認は `WebhookClient` に集めています。

[MCP Eventsの公式仕様](https://developers.openai.com/plugins/build/mcp-events)
