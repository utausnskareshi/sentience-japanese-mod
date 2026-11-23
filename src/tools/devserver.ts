/**
 * 開発用のローカルHTTPサーバ（ブラウザでの表示確認用）。
 *
 * 使い方: npm run build && npm run dev -- [--game <path>] [--port 5635]
 *
 * ゲームの www フォルダを読み取り専用で配信し、次のファイルだけを差し替える。
 * ゲームフォルダには一切書き込まないため、パッチを当てずに日本語化の表示を確認できる。
 *   /js/plugins.js            … 元のファイルに本プラグインの登録を追加したもの
 *   /js/plugins/SentienceJP.js … dist/payload のビルド結果
 *   /sentience_jp/*           … dist/payload の訳文バンドル
 * 127.0.0.1 のみで待ち受ける（外部には公開しない）。Host ヘッダが 127.0.0.1:<port> / localhost:<port> 以外の要求は
 * 403 で拒否する（DNS リバインディングで外部のページから読まれないようにするため）。
 * 振り分けと Host の検査の規則は lib/devserver-lib.ts（単体テストあり）。
 */
import { createServer, type ServerResponse } from 'node:http';
import { existsSync, readFileSync, statSync } from 'node:fs';
import { extname, join } from 'node:path';
import { locateGame } from './lib/game.ts';
import { PAYLOAD_GAME_DIR, addPluginEntry } from './lib/payload.ts';
import { parseCliArgs } from './lib/cli.ts';
import { isAllowedHost, routeRequest } from './lib/devserver-lib.ts';

/** 拡張子ごとの Content-Type */
const MIME: Readonly<Record<string, string>> = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'application/javascript; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.png': 'image/png',
  '.ttf': 'font/ttf',
  '.ogg': 'audio/ogg',
  '.m4a': 'audio/mp4',
  '.txt': 'text/plain; charset=utf-8',
};

/** 既定の待ち受けポート */
const DEFAULT_PORT = 5635;

/** 使い方（引数の誤りのときに表示する） */
const USAGE = 'npm run dev -- [--game <ゲームのフォルダ>] [--port <番号（既定 5635）>]';

/**
 * 500 応答の本文（固定の文言）。例外の文にはユーザー名を含みうる絶対パスが入るため、本文には含めず、
 * 詳細はサーバのコンソールにだけ出す。
 */
const INTERNAL_ERROR_BODY = 'サーバ内部エラー（詳細は開発用サーバのコンソールを確認してください）';

/** テキストの応答を返す */
const sendText = (res: ServerResponse, status: number, text: string): void => {
  res.writeHead(status, { 'Content-Type': 'text/plain; charset=utf-8', 'Cache-Control': 'no-store' });
  res.end(text);
};

/** 本文を返す（キャッシュさせない） */
const sendBody = (res: ServerResponse, contentType: string, body: Buffer | string): void => {
  res.writeHead(200, { 'Content-Type': contentType, 'Cache-Control': 'no-store' });
  res.end(body);
};

/** ファイルを送信する（無ければ 404） */
const sendFile = (res: ServerResponse, path: string | undefined): void => {
  if (path === undefined || !existsSync(path) || !statSync(path).isFile()) {
    sendText(res, 404, 'Not Found');
    return;
  }
  // 読み込みに失敗したときに 500 を返せるよう、ヘッダを送る前に読み込む
  const body = readFileSync(path);
  sendBody(res, MIME[extname(path).toLowerCase()] ?? 'application/octet-stream', body);
};

/** --port の値を検証して返す（指定が無ければ既定のポート） */
const parsePort = (raw: string | undefined): number => {
  const port = Number(raw ?? String(DEFAULT_PORT));
  if (!Number.isInteger(port) || port < 1 || port > 65535) {
    throw new Error(`--port には 1〜65535 の整数を指定してください（指定値: ${raw ?? ''}）`);
  }
  return port;
};

const main = (): void => {
  // 未知のオプション・値の無い --game / --port は使い方を表示して終了する（lib/cli.ts）。
  // 以前は値の無い --port が既定の 5635 で起動し、値の無い --game が自動検出に切り替わっていた
  const { values } = parseCliArgs(process.argv.slice(2), { usage: USAGE, options: { game: 'string', port: 'string' }, allowPositionals: false });
  const game = locateGame(values.game);
  const port = parsePort(values.port);
  const overlayWww = join(PAYLOAD_GAME_DIR, 'www');
  if (!existsSync(join(overlayWww, 'js', 'plugins', 'SentienceJP.js'))) {
    throw new Error('dist/payload が見つかりません。先に npm run build を実行してください');
  }

  const server = createServer((req, res) => {
    // DNS リバインディング対策: Host がこのサーバ自身（127.0.0.1 / localhost と待ち受けのポート）でなければ拒否する。
    // Host の値は外部からの入力なので、コンソールには JSON 文字列として（制御文字をエスケープして）出す
    if (!isAllowedHost(req.headers.host, port)) {
      console.error(`Host ヘッダが許可されていない要求を拒否しました: Host=${JSON.stringify(req.headers.host ?? null)} ${JSON.stringify(req.url ?? '')}`);
      sendText(res, 403, `Forbidden（http://127.0.0.1:${port}/ または http://localhost:${port}/ で開いてください）`);
      return;
    }
    try {
      const route = routeRequest(req.url ?? '/', { gameWww: game.www, overlayWww });
      if (route.kind === 'plugins-js') {
        // 元の plugins.js に登録を追加して返す（ファイルは書き換えない）
        const patched = addPluginEntry(readFileSync(route.path, 'utf8'));
        sendBody(res, MIME['.js'] ?? 'application/javascript', patched);
        return;
      }
      sendFile(res, route.path);
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      console.error(`要求の処理に失敗しました: ${req.url ?? ''}: ${message}`);
      if (res.headersSent) {
        // ヘッダ送信後はステータスを変えられないため、接続を切って異常を伝える
        res.destroy();
        return;
      }
      // 本文は固定の文言にする（例外の文の絶対パスを外に出さない。詳細は上のコンソール出力のみ）
      sendText(res, 500, INTERNAL_ERROR_BODY);
    }
  });
  server.on('error', (error: Error) => {
    // ポートの使用中（EADDRINUSE）など。プロセスを異常終了させず、理由を表示する
    console.error(`開発用サーバでエラーが発生しました: ${error.message}`);
    process.exitCode = 1;
    server.close();
  });
  server.listen(port, '127.0.0.1', () => {
    console.log(`開発用サーバ: http://127.0.0.1:${port}/ （ゲーム: ${game.www}）`);
  });
};

try {
  main();
} catch (error) {
  console.error(error instanceof Error ? error.message : String(error));
  process.exitCode = 1;
}
