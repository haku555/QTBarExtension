# QTBarExtension

Windows 11 File Explorerのパス欄の下にブックマークバーを追加するツール。  
BandObject（QTTabBarの旧方式）を使わず、**WinEvent Hook + オーバーレイウィンドウ方式**で実装しているため、Windows Updateで壊れにくい。

---

## 機能

| 機能 | 説明 |
|------|------|
| **タブグループ** | 複数のフォルダをグループとして保存し、グループタブで切り替え |
| **ブックマークバー** | パス欄の下にフォルダへのショートカットを表示 |
| **フォルダ履歴** | 🕐ボタンで最近開いたフォルダ一覧を表示 |
| **現在フォルダを追加** | ＋ボタンで今開いているフォルダをブックマークに追加 |
| **自動追従** | Explorerの移動・リサイズ・最大化に自動追従 |
| **ExplorerPatcher対応** | 依存関係なし、EPの有無に関係なく動作 |
| **Shift+ホイール横スクロール** | フォルダビュー上でShiftを押しながらホイールすると横スクロール |
| **省略ファイル名のフルネーム表示** | 詳細表示で省略されたファイル名をホバーで完全表示 |
| **ファイルプレビュー** | 画像/動画/音声/テキストをホバーでツールチップ風プレビュー |
| **ショートカット作成** | トレイメニューからデスクトップにショートカットを作成 |
| **自動実行登録** | トレイメニューからWindows起動時の自動実行をON/OFF切り替え |

---

## ファイルプレビュー機能

ファイル一覧でアイテムにマウスを乗せると、設定した待機時間後にプレビューウィンドウが表示されます。

- **画像**: jpg/png/gif/bmp/webp等。サムネイル＋寸法を表示
- **動画・音声**: mp4/mp3等。再生し、再生位置を記録（再訪問時に再開）
- **テキスト**: txt/md/json/cs等。先頭N KiBをシンタックスなしで表示（フォント・色は設定可）
- **ネットワークパス (`\\server\share\...`)** にも対応（設定でオン/オフ可）
- **圧縮フォルダ (zip) 内のファイル**にも対応（`archive.zip\path\to\file.png` のように展開してプレビュー）
- フォルダウィンドウが非アクティブでも表示可能（設定）
- 画像はLRUキャッシュ（既定256MiB）。設定画面からキャッシュクリア可能

### 既定値

| 設定項目 | 既定値 |
|---|---|
| プレビュー表示までの待機時間 | 300 ms |
| プレビューウィンドウの不透明度 | 100% |
| 画像キャッシュ最大サイズ | 256 MiB |
| 画像の最大幅/高 | 512 / 256 px |
| 動画の最大幅/高 | 512 / 256 px |
| テキストの最大幅/高 | 256 / 256 px |
| テキスト読み込みサイズ | 1 KiB |

設定はトレイアイコン → 設定 → 「プレビュー: 全般」「プレビュー: 拡張子/フォント」「プレビュー: ウィンドウ」の各タブから変更できます。

---

## ビルド方法

### 必要環境
- .NET 10 SDK（Windows）
- Visual Studio 2022 または `dotnet` CLI

### ビルド

```powershell
cd QTBarExtension
dotnet build -c Release
```

### 実行

```powershell
dotnet run
# または
.\bin\Release\net10.0-windows\QTBarExtension.exe
```

---

## かんたんセットアップ（初回起動後）

ビルドした `QTBarExtension.exe` を一度起動すると、タスクトレイにアイコンが常駐します。  
このトレイアイコンを右クリックすると、次の項目から普段使いを簡単にできます。

| メニュー項目 | 内容 |
|---|---|
| **デスクトップにショートカットを作成** | `QTBarExtension.exe` へのショートカット(.lnk)をデスクトップに作成します。次回からはダブルクリックだけで起動できます。 |
| **Windows起動時に自動実行** | チェックを入れると、サインイン時に自動でバックグラウンド起動するようになります（レジストリ `HKCU\...\Run` に登録。管理者権限不要）。チェックを外すといつでも解除できます。 |
| **設定** | タブ・ブックマーク・プレビューなどの各種設定画面を開きます。 |
| **終了** | アプリを終了します。 |

> 実行ファイルを別のフォルダに移動した場合は、ショートカットや自動実行の登録をいったん解除してから作り直してください（登録は実行時のファイルパスを記録する方式のため）。

---

## 仕組み

```
QTBarExtension
  │
  ├── ExplorerWatcher          WinEvent Hook で全Explorerウィンドウを監視
  │     ├── SetWinEventHook    移動・リサイズ・フォーカス・開閉 を検知
  │     └── ExplorerWindowInfo ReBarWindow32 の位置からバー挿入点を計算
  │
  ├── OverlayBar (WPF Window)  パス欄の下に追従するバーウィンドウ
  │     ├── グループタブ       TabGroup の切り替え
  │     ├── ブックマーク       クリックで Shell.Application 経由ナビゲート
  │     └── 履歴              最近開いたパスの一覧
  │
  ├── ExplorerViewEnhancer     フォルダビュー拡張
  │     ├── Shift+ホイール横スクロール (WH_MOUSE_LL + WM_MOUSEHWHEEL転送)
  │     └── フルネームツールチップ (LVM_SUBITEMHITTEST / LVM_GETITEMTEXTW)
  │
  ├── PreviewHoverService      ファイルプレビュー
  │     ├── PreviewContentProvider  画像/動画/音声/テキスト読込・キャッシュ・zip展開
  │     └── PreviewPopupWindow      プレビュー表示ウィンドウ
  │
  └── SettingsStore            %AppData%\QTBarExtension\settings.json に保存
```

### なぜ壊れにくいのか

QTTabBarは `BandObject`（IE時代のCOM API）を使っていたため、  
MicrosoftがExplorerのレンダリングを変更するたびに壊れていた。

本ツールは以下のAPIのみを使用：
- `SetWinEventHook` … Win32の基礎API、30年変わっていない
- `SetWindowPos` … 同上
- `EnumChildWindows` + `GetClassName` … Explorerのウィンドウ構造走査
- `SendMessage` (LVM_*) … SysListView32への標準メッセージ
- `WH_MOUSE_LL` … 標準のグローバルマウスフック

これらはWindowsの根幹APIで、今後も互換性が維持される可能性が高い。

---

## データの場所

```
%AppData%\QTBarExtension\
  └── settings.json   ← タブグループ・ブックマーク・履歴・プレビュー設定
```

プレビューの一時展開ファイル（圧縮フォルダ内の動画・音声）:
```
%TEMP%\QTBarExtension_Preview\
```

---

## 今後の拡張予定

- [ ] 動画・画像のサムネイルプレビューのハードウェアデコード最適化
- [ ] ダークモード対応（プレビューウィンドウ）
- [ ] タブグループのドラッグ&ドロップ並び替え
- [ ] フォルダアイコンの色分け表示

---

## 既知の制約・注意点

- `ExplorerViewEnhancer` / `PreviewHoverService` はクロスプロセスでSysListView32の
  `LVM_GETITEMTEXTW` 等を呼び出すため、`LVITEMW`構造体オフセットは環境依存の概算値です。
  実機での動作確認・調整を推奨します。
- Shift+ホイールの横スクロールは `WM_MOUSEHWHEEL` をSysListView32へ転送する方式です。
  環境によって動作しない場合は水平スクロールバーへの`WM_HSCROLL`送信に切り替えが必要です。

---

## .NET バージョンポリシー

| バージョン | 種別 | EOL | 備考 |
|---|---|---|---|
| .NET 8  | LTS | 2026年11月 | ⚠️ もうすぐ終了 |
| .NET 9  | STS | 2026年11月 | ⚠️ もうすぐ終了 |
| **.NET 10** | **LTS** | **2028年11月** | **← 本プロジェクトが使用** |
| .NET 11 | STS | 2027年11月予定 | 2026年11月リリース予定 |
| .NET 12 | LTS | 2030年11月予定 | 2027年11月リリース予定 |

**次のLTSへの移行手順（将来用）:**
```
QTBarExtension.csproj の1行を変更するだけ:
  net10.0-windows  →  net12.0-windows
```
