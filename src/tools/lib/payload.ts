/**
 * 配布ペイロード（パッチツールに埋め込むファイル群）の定義と、plugins.js への登録処理。
 *
 * ペイロードの構成（パッチツール側と共有する契約）:
 *   dist/payload/manifest.json
 *   dist/payload/game/www/js/plugins/SentienceJP.js
 *   dist/payload/game/www/sentience_jp/translation.json
 */
import { join } from 'node:path';

/** ペイロードの出力先 */
export const PAYLOAD_DIR = join('dist', 'payload');

/** ゲームフォルダに配置するファイルの出力先（ゲームのルートに対応） */
export const PAYLOAD_GAME_DIR = join(PAYLOAD_DIR, 'game');

/** プラグインファイルの相対パス（ゲームのルートから） */
export const PLUGIN_RELATIVE_PATH = 'www/js/plugins/SentienceJP.js';

/** 訳文バンドルの相対パス（ゲームのルートから） */
export const BUNDLE_RELATIVE_PATH = 'www/sentience_jp/translation.json';

/** plugins.js に追加するエントリ */
export const PLUGIN_ENTRY = {
  name: 'SentienceJP',
  status: true,
  description: 'Sentience 日本語化パッチ（非公式）',
  parameters: {},
} as const;

/**
 * plugins.js の内容に本プラグインの登録を追加した文字列を返す（純粋関数）。
 * 既に登録済みならそのまま返す。形式が想定外なら理由つきで例外を投げる。
 */
export const addPluginEntry = (source: string): string => {
  if (source.includes(`"name":"${PLUGIN_ENTRY.name}"`)) {
    return source;
  }
  const match = /\}(\s*)\](\s*;?\s*)$/.exec(source);
  if (match === null) {
    throw new Error('plugins.js の末尾（"}\\n];"）が想定と異なるため、プラグインを登録できません');
  }
  const newline = source.includes('\r\n') ? '\r\n' : '\n';
  const insertAt = match.index + 1; // 最後の "}" の直後
  return `${source.slice(0, insertAt)},${newline}${JSON.stringify(PLUGIN_ENTRY)}${source.slice(insertAt)}`;
};
