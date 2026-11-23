/**
 * 実行環境（ゲームのグローバル）へ安全にアクセスするための補助関数群。
 *
 * 他プラグインが定義するオブジェクトは存在しない可能性があるため、unknown として取り出し、
 * 型を確かめてから使う。フックの差し込みに失敗しても、ゲーム自体は止めずに
 * 理由をコンソールへ出力して該当機能だけを無効にする（エラーは握りつぶさない）。
 */

/** 任意のメソッド（this と引数は unknown として扱う） */
export type Method = (this: unknown, ...args: unknown[]) => unknown;

/** プラグイン名（ログの接頭辞） */
const TAG = '[SentienceJP]';

/** プレーンなオブジェクト（配列・null 以外）か */
export const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value);

/** 関数か（メソッドとして呼べるか） */
export const isMethod = (value: unknown): value is Method => typeof value === 'function';

/** グローバルオブジェクト（window）を Record として取り出す */
export const globals = (): Record<string, unknown> => window as unknown as Record<string, unknown>;

/**
 * グローバルからドット区切りのパスで値を取り出す（例: "Yanfly.MMM.Name"）。
 * 途中で見つからなければ undefined。
 */
export const getGlobalPath = (path: string): unknown =>
  path.split('.').reduce<unknown>((acc, key) => (isRecord(acc) || isMethod(acc) ? (acc as Record<string, unknown>)[key] : undefined), globals());

/** グローバルのクラス（コンストラクタ関数）の prototype を取り出す */
export const getPrototype = (className: string): Record<string, unknown> | undefined => {
  const ctor = globals()[className];
  if (!isMethod(ctor)) {
    return undefined;
  }
  const proto: unknown = (ctor as unknown as { prototype: unknown }).prototype;
  return isRecord(proto) ? proto : undefined;
};

/** 警告をコンソールへ出力する */
export const warn = (message: string): void => {
  console.warn(`${TAG} ${message}`);
};

/** 発生したエラーの件数（デバッグ表示用） */
let errorCount = 0;

/** これまでに報告したエラーの件数 */
export const reportedErrorCount = (): number => errorCount;

/**
 * 実行中のエラーを報告する。翻訳処理の失敗でゲームを止めないよう、
 * 呼び出し側は原文表示などにフォールバックしたうえでこの関数を呼ぶ。
 *
 * @param context 失敗した処理の説明
 * @param error 発生した例外
 * @param fallback 失敗後にどう表示を続けるかの説明（既定は「原文で表示を続けます」。
 *   フォントの読み込み失敗のように原文表示にならない場合は、実際の挙動を書く。EA-01）
 */
export const reportError = (context: string, error: unknown, fallback = '原文で表示を続けます'): void => {
  errorCount += 1;
  const detail = error instanceof Error ? `${error.message}\n${error.stack ?? ''}` : String(error);
  console.error(`${TAG} ${context} に失敗しました（${fallback}）: ${detail}`);
};

/**
 * オブジェクトのメソッドを差し替える。
 * 対象メソッドが無い場合は警告を出して false を返す（その機能だけ無効になる）。
 *
 * @param target メソッドを持つオブジェクト（prototype など）
 * @param name メソッド名
 * @param wrap 元のメソッドを受け取り、新しいメソッドを返す関数
 * @param where ログ用の説明（例: "Window_QuestInfo.prototype"）
 */
export const wrapMethod = (
  target: Record<string, unknown> | undefined,
  name: string,
  wrap: (original: Method) => Method,
  where: string,
): boolean => {
  if (target === undefined) {
    warn(`${where} が見つからないため ${name} の日本語化を省略します`);
    return false;
  }
  const original = target[name];
  if (!isMethod(original)) {
    warn(`${where}.${name} が見つからないため日本語化を省略します`);
    return false;
  }
  target[name] = wrap(original);
  return true;
};

/** 日本語（かな・漢字・全角記号）を含むか */
export const containsJapanese = (text: string): boolean => /[　-ヿ㐀-䶿一-鿿＀-￯]/.test(text);
