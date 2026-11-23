/**
 * 開発用サーバ（devserver.ts）の判定部分（純粋関数）。要求の振り分けと Host ヘッダの検査を単体テストで確かめる。
 */
import { join } from 'node:path';
import { resolveSafe } from './paths.ts';

/** 配信するフォルダ */
export interface ServeRoots {
  /** ゲームの www フォルダ（読み取り専用で配信する） */
  readonly gameWww: string;
  /** ビルド結果の www（dist/payload/game/www。プラグインと訳文バンドル） */
  readonly overlayWww: string;
}

/** 要求の振り分け先 */
export type Route =
  /** 配信フォルダ内のファイル（path が undefined なら配信できないパスなので 404） */
  | { readonly kind: 'file'; readonly path: string | undefined }
  /** 元の plugins.js（本プラグインの登録を追加して返す。ファイルは書き換えない） */
  | { readonly kind: 'plugins-js'; readonly path: string };

/**
 * 要求の URL（クエリ付き可）を振り分ける（純粋関数）。次のものだけをビルド結果から返し、それ以外はゲームの www から返す。
 *   /                          … ゲームの index.html
 *   /js/plugins.js             … 元のファイルに本プラグインの登録を追加したもの
 *   /js/plugins/SentienceJP.js … dist/payload のビルド結果
 *   /sentience_jp/*            … dist/payload の訳文バンドル
 * フォルダ外への脱出は resolveSafe（paths.ts）で防ぐ。
 */
export const routeRequest = (url: string, roots: ServeRoots): Route => {
  const urlPath = url.split('?')[0] ?? '/';
  if (urlPath === '/' || urlPath === '') {
    return { kind: 'file', path: join(roots.gameWww, 'index.html') };
  }
  if (urlPath === '/js/plugins.js') {
    return { kind: 'plugins-js', path: join(roots.gameWww, 'js', 'plugins.js') };
  }
  if (urlPath === '/js/plugins/SentienceJP.js' || urlPath.startsWith('/sentience_jp/')) {
    return { kind: 'file', path: resolveSafe(roots.overlayWww, urlPath) };
  }
  return { kind: 'file', path: resolveSafe(roots.gameWww, urlPath) };
};

/**
 * Host ヘッダが、このサーバ自身（127.0.0.1 または localhost と、待ち受けのポート）を指しているか（純粋関数）。
 * DNS リバインディング（攻撃者のドメインを 127.0.0.1 に向け直し、ブラウザに同一オリジンとして読ませる攻撃）では
 * Host が攻撃者のドメインになるため、ここで拒否できる。
 * ブラウザは既定（80）以外のポートでは Host にポートを付けるため "127.0.0.1:<port>" と "localhost:<port>" を許可し、
 * ポート 80 のときだけポートなしの形も許可する。名前の大文字小文字は区別しない。
 */
export const isAllowedHost = (host: string | undefined, port: number): boolean => {
  if (host === undefined) {
    return false;
  }
  const value = host.trim().toLowerCase();
  return ['127.0.0.1', 'localhost'].some((name) => value === `${name}:${port}` || (port === 80 && value === name));
};
