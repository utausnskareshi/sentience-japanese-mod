/**
 * 開発用サーバで、URL のパスを配信フォルダ内のファイルパスに変換する処理（純粋関数）。
 * devserver.ts から使い、フォルダ外への脱出を防げていることを単体テストで確かめる。
 */
import { join } from 'node:path';

/**
 * URL のパス（クエリ付き可）を、基準フォルダ配下のファイルパスに変換する。
 * 配信してよいパスでなければ undefined を返す（呼び出し側は 404 にする）。
 *
 * 拒否するもの:
 * - 不正なパーセントエンコード（decodeURIComponent が URIError を投げるもの）
 * - デコード後に ".." を含むもの（%2e%2e のようにエンコードされたものも含む）
 * - バックスラッシュ（%5c）… Windows では区切り文字として解釈され、".." 判定をすり抜けるため
 * - コロン … ドライブ指定（C:）や NTFS の代替データストリーム（file:stream）を防ぐ
 * - NUL 文字
 * 先頭や連続する "/" は空の要素として無視するため、UNC パス（//server/share）にもならない。
 * ゲームが要求するファイル（img/・audio/・data/・js/・fonts/ など）はいずれにも該当しない。
 */
export const resolveSafe = (base: string, urlPath: string): string | undefined => {
  const rawPath = urlPath.split('?')[0] ?? '';
  let decoded: string;
  try {
    decoded = decodeURIComponent(rawPath);
  } catch (error) {
    if (error instanceof URIError) {
      // 不正なエンコードはファイル名として解釈できないため、配信しない
      return undefined;
    }
    throw error;
  }
  if (/[\\:\0]/.test(decoded)) {
    return undefined;
  }
  const segments = decoded.split('/').filter((segment) => segment !== '' && segment !== '.');
  if (segments.includes('..')) {
    return undefined;
  }
  return segments.length === 0 ? base : join(base, ...segments);
};
