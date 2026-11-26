# ロードマップ

ここは承認済みマイルストーンの ID・目的・優先度・依存関係の正本（main 掲載版）である。登録・統合時点の状態は [PROGRESS](PROGRESS.md)、個別の最新作業記録の参照方法は [開発運用手順](operations/development-workflow.md) を参照する。

## 現在の定義

| ID | 名称 | 優先度・着手条件 | 依存 ID | 作業ブランチ（未着手は予定名） | 詳細 |
| --- | --- | --- | --- | --- | --- |
| MGMT-01 | 進捗管理手法の策定 | 管理基盤 | なし | `milestone/MGMT-01-progress-management` | [MGMT-01](milestones/MGMT-01.md) |
| MGMT-02 | main 統合規則の改定・アプリマイルストーン登録 | 管理基盤 | MGMT-01 | `milestone/MGMT-02-merge-policy-and-roadmap` | [MGMT-02](milestones/MGMT-02.md) |
| M03 | 羽衣ちゃんの表示・ウィンドウの見た目の調整 | 最高 | 未確定（下記参照） | `milestone/M03-character-and-window-ui` | [M03](milestones/M03.md) |
| M04 | Dotからの能動的な発言 | 高 | 未確定（下記参照） | `milestone/M04-dot-proactive-messages` | [M04](milestones/M04.md) |
| M05 | 他会話の回答受信・会話管理基盤 | 高（Workのひな型を先行、Chat調査は独立） | M03・M04（完了）、M08の実行基盤 | `milestone/M05-other-conversations` | [M05](milestones/M05.md) |
| M06 | 通信状態の管理・障害復旧と購読窓の表示 | 中 | M03 の基本 UI（完了）、詳細依存は設計時に確認 | `milestone/M06-connection-status` | [M06](milestones/M06.md) |
| MGMT-07 | デスクトップ追加機能のマイルストーン登録 | 管理基盤 | MGMT-02 | `milestone/MGMT-07-register-desktop-behaviors` | [MGMT-07](milestones/MGMT-07.md) |
| M08 | 起動・終了管理とスタートアップによる自動起動 | 高 | M03 の基本 UI を先行（詳細依存は未確定） | `milestone/M08-startup` | [M08](milestones/M08.md) |
| M09 | メッセージ・マウスイベントに応じた羽衣ちゃんのアクション | 中（早期並行着手候補） | M03（表示・操作） | `milestone/M09-character-reactions` | [M09](milestones/M09.md) |
| M10 | 時間経過に応じたデスクトップ上のランダム移動 | 低 | M03（位置制御）、M09 は未確定 | `milestone/M10-autonomous-desktop-movement` | [M10](milestones/M10.md) |
| MGMT-11 | M08 の起動管理範囲・優先度改定 | 管理基盤 | MGMT-07 | `milestone/MGMT-11-expand-startup-scope` | [MGMT-11](milestones/MGMT-11.md) |
| MGMT-12 | M06 の通信管理・障害復旧範囲と優先度改定 | 管理基盤 | MGMT-11 | `milestone/MGMT-12-expand-communication-recovery` | [MGMT-12](milestones/MGMT-12.md) |
| MGMT-13 | 新着解除改善マイルストーンの登録 | 管理基盤 | MGMT-12 | `milestone/MGMT-13-register-new-message-clearing` | [MGMT-13](milestones/MGMT-13.md) |
| M14 | 履歴閲覧操作に応じた新着解除 | 低 | M03（会話・履歴UI、完了）。M06との整合は設計時に確認 | `milestone/M14-new-message-clearing` | [M14](milestones/M14.md) |
| MGMT-15 | M04・M06の優先度改定とM09の早期並行着手方針 | 管理基盤 | MGMT-13、M08 | `milestone/MGMT-15-reprioritize-proactive-and-reactions` | [MGMT-15](milestones/MGMT-15.md) |
| MGMT-16 | M06への購読元接続不一致・ツール更新後の復旧課題の追記 | 管理基盤 | MGMT-15 | `milestone/MGMT-16-record-subscription-connection-mismatch` | [MGMT-16](milestones/MGMT-16.md) |
| MGMT-17 | 他会話アクセスの3分割・クラウド対象限定 | 管理基盤 | MGMT-16、M04 | `milestone/MGMT-17-split-cloud-conversation-access` | [MGMT-17](milestones/MGMT-17.md) |
| M18 | 複数会話スレッドの切り替えUI・起動時の入力窓 | 高 | M03、M05 | `milestone/M18-conversation-switching-ui` | [M18](milestones/M18.md) |
| M19 | 他会話への発言送信 | 高 | M05、M18 | `milestone/M19-cloud-conversation-send` | [M19](milestones/M19.md) |
| MGMT-20 | M18へ起動時の入力窓の改善を記録 | 管理 | M18の登録 | `milestone/MGMT-20-record-startup-input` | [MGMT-20](milestones/MGMT-20.md) |
| M21 | キャラクターアクションの順番・中断制御と終了演出 | 優先度は着手時に確認 | M09、終了経路はM08 | `milestone/M21-action-sequencing` | [M21](milestones/M21.md) |
| M22 | 会話吹き出しの本文枠の位置合わせ・長文表示 | 優先度は着手時に確認 | M03 | `milestone/M22-bubble-body-alignment` | [M22](milestones/M22.md) |
| MGMT-23 | M05の作業分離とSSP追加要望の登録 | 管理 | MGMT-20、M05・M03・M08・M09の定義 | `milestone/MGMT-23-register-ssp-followups` | [MGMT-23](milestones/MGMT-23.md) |
| MGMT-24 | M22に長文最終行のはみ出し修正を記録 | 管理 | M22の登録 | `milestone/MGMT-24-record-bubble-overflow` | [MGMT-24](milestones/MGMT-24.md) |

| MGMT-25 | 履歴表示・背景透過・画像送受信の追加要望を登録 | 管理 | MGMT-24、M14・M22の定義 | `milestone/MGMT-25-register-history-timeout-and-window-opacity` | [MGMT-25](milestones/MGMT-25.md) |
| M26 | 新着でない履歴カードの時間経過による非表示 | 優先度は着手時に確認 | M03、M14の新着判定と整合 | `milestone/M26-history-card-timeout` | [M26](milestones/M26.md) |
| M27 | 会話履歴窓・入力窓の背景透過 | 優先度は着手時に確認 | M03、M22の表示と整合 | `milestone/M27-conversation-window-opacity` | [M27](milestones/M27.md) |

| M28 | 会話履歴のMarkdown表示 | 優先度は着手時に確認 | M03、M22の改ページと整合 | `milestone/M28-history-markdown` | [M28](milestones/M28.md) |
| M29 | 画像ファイルの送受信 | 優先度は着手時に確認 | M03、M04の送受信。追加連携は設計時に確認 | `milestone/M29-image-attachments` | [M29](milestones/M29.md) |

| MGMT-30 | 継続指示の記録・再確認と動作確認の依頼ルール | 管理 | MGMT-25 | `milestone/MGMT-30-record-standing-verification-rules` | [MGMT-30](milestones/MGMT-30.md) |
| MGMT-31 | 購読窓の表示調整と履歴ブックマークの登録 | 管理 | MGMT-30、M06 | `milestone/MGMT-31-register-status-and-bookmarks` | [MGMT-31](milestones/MGMT-31.md) |
| M32 | 会話履歴のブックマークと呼び出し | 優先度は着手時に確認 | M03・M14、M26／M18との整合を確認 | `milestone/M32-history-bookmarks` | [M32](milestones/M32.md) |
| M33 | 状況に応じたキャラクターアクションの選択 | 優先度は着手時に確認 | M21（再生・中断）、移動の統合にはM10 | `milestone/M33-contextual-action-selection` | [M33](milestones/M33.md) |
| MGMT-34 | M21の範囲変更と状況に応じた動作選択の登録 | 管理 | M21、MGMT-31 | `milestone/MGMT-34-split-action-selection` | [MGMT-34](milestones/MGMT-34.md) |
| M35 | 長文の会話窓の高さ調整と全文表示 | 優先度は着手時に確認 | M03・M22（既存UI・左右位置） | `milestone/M35-bubble-text-fit` | [M35](milestones/M35.md) |
| MGMT-36 | 長文の会話窓の再発対応を登録 | 管理 | M22、MGMT-34 | `milestone/MGMT-36-register-bubble-text-fit` | [MGMT-36](milestones/MGMT-36.md) |
| M37 | 羽衣ちゃんのイラストの髪の隙間の透過修正 | 優先度は着手時に確認 | M03の立ち絵、M09のアクション素材 | `milestone/M37-hair-gap-alpha` | [M37](milestones/M37.md) |
| MGMT-38 | イラストの透過修正を登録 | 管理 | MGMT-36 | `milestone/MGMT-38-register-hair-gap-alpha` | [MGMT-38](milestones/MGMT-38.md) |
| MGMT-39 | キャラクター動作の拡張方針を恒久化 | 管理 | M21、MGMT-30 | `milestone/MGMT-39-action-extensibility-rule` | [MGMT-39](milestones/MGMT-39.md) |

## 着手順と依存

- M03 はアプリの必須機能であり、最優先で基本 UI を実用段階に進める。M04～M06・M08～M10 は原則その後に検討する。M03 の完了自体を一律の技術的依存とはせず、完了前の並行着手は必要性を整理しユーザーと合意する。
- MGMT-15時点の経緯: M03（最高）・M08（高）はユーザー受入に基づき完了。M04は高、M05・M06・M09は中、M10・M14は低とした（現行のM05・M18・M19の優先度は後述のMGMT-17を参照）。M08の初期化通知補完・stdioパス修正後に会話往復の成功を確認しており、M06の緊急性は下がった。次はM04を優先し、M09も早期に並行着手できる候補とする。M09の優先度は中のまま維持する。M04とM09に相互の完了を一律の着手条件とする技術依存は設定しない。各自の専用枝・担当・検証を分離し、共通のメッセージ受信イベント境界を設計時に合意する。M04を先に統合することを第一候補とし、後から統合する枝は最新mainを取り込み、結合回帰を確認する。実装・並行着手は別途指示を受けて開始する。M09はM03の表示・操作、M10はM03の位置制御を基礎とする。M09とM10の技術依存の詳細は着手時に確認する。
- 2026-11-25 のユーザー指示により、起動時の全体ビルドと稼働中 DLL のロック、トンネル配下のプロセス再生成に関する運用改善を M08 に集約し、優先度を中から高に変更した。M03 の追加実装課題にはしない。
- 2026-11-25 のユーザー指示により、M06 を通信状態の可視化に加え、購読の管理、診断、配信済み未回答の扱い、安全な障害復旧を含む範囲に拡張し、優先度を低から高へ変更した。M03 のデザイン調整は完了として区切る。
- 2026-11-25 の追加指示（MGMT-15）により、M06を高から中、M04を中から高へ変更する。M06の範囲は縮小しない。M09は中を維持し、M04と早期に並行着手する候補とする。
- 2026-11-26 の追加指示（MGMT-16）により、M04で受領したツール更新後の購読元接続不一致をM06の障害例・追加完了条件として記録する。M06の中優先度・未着手は維持する。
- M14 は履歴のアクティブ化・スクロール操作による新着解除を扱う。M06 の通信状態の区別・障害復旧とは分離し、低優先度とする。M03 の完了は維持する。
- 2026-11-26 のユーザー承認（MGMT-17）により、M05を高優先度の他会話の回答受信・会話管理基盤へ改定し、M18（複数会話スレッドの切り替えUI）、M19（他会話への発言送信）を高優先度で登録する。当面の外部会話対象はクラウドChat／クラウドWorkのみ。ローカルWorkは対象外。M05の初期工程で受信・将来の送信先識別の実現可能性を種別ごとに調査し、対応不能な範囲はユーザーとの合意を経て変更する。M05 → M18 → M19の順に基盤と実装・統合を進める。M09は既存担当で継続し、M05の調査と並行できるが共有箇所の実装は調整する。M06・M09は中、M10・M14は低のまま維持する。今回の3分割は定義・登録のみであり、アプリ実装の着手・新たな会話の作成は別途行う。
- MGMT-23: M05のクラウドWork取得と通常Chat本文調査を別作業として追跡する。Workのひな型を先行し、Chat調査は余力があるときに並行する。操作はSSP内で完結し、M05全体の完了条件は縮小しない。M21はM09、M22はM03と関連するが、いずれも完了済み範囲の履歴を開き直さない後続項目とする。M21の終了演出はM08の正常終了経路と協調する。
- MGMT-25: 履歴カードの時間経過による非表示は M26、履歴窓・入力窓の背景透過は M27。M14 は閲覧操作による新着解除のまま、M22 は位置合わせ・長文表示のまま。Markdown表示は M28、画像ファイル送受信は M29。共有する表示箇所は統合時に回帰を確認する。
- アプリ本体マイルストーンの名称・目的・優先度はユーザーの提示・承認に基づく。詳細な仕様・完了条件は着手時に確定する。個別記録の完了条件は暫定案であり、未承認の仕様追加を意味しない。
- 既存の通信実証は現行実装の成果として扱い、新たなアプリ本体マイルストーンにはしない。

## 更新方針

- 管理系 ID は `MGMT-01` 形式、アプリ本体は `M03` 形式とする。接頭辞にかかわらず数字部分は全マイルストーンで共通の連番とする。登録済みの番号は完了・保留・中止等の状態にかかわらず永久に再利用しない。欠番は詰めない。
- 新規採番は管理窓口で直列化する。main の一覧・個別記録、登録履歴、予約済みの ID、作業枝の一覧を確認し、確保済みの最大番号に1を加えた番号を確保する。最低2桁とし、100以降は3桁以上を用いる。登録・予約済み ID は変更しない。未登録の暫定案は正式登録前に変更できる。名称変更や範囲変更は個別記録に履歴を残す。
- 完了・中止等の ID も一覧に残し、廃止時に番号を解放しない。
- 優先度は着手順の目安、依存関係は着手・統合の条件として別々に扱う。依存グラフに循環を作らない。
- 新規登録時はユーザーと合意した目的、範囲、完了条件、依存関係を個別文書に定義する。未確定事項は明記する。
- 完了済みの行も履歴として残す。追加・修正・登録は管理系等のマイルストーンのマージで反映する。
