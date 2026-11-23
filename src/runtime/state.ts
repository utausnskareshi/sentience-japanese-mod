/**
 * 表示言語の状態と、データ側の書き換え（差し替え）記録を管理するモジュール。
 *
 * - 会話・選択肢・アクター名・クエスト文は「表示する直前」に翻訳するため、ここでは扱わない。
 * - システム用語・アイテム名・メニュー名など、セーブデータに保存されないデータは
 *   読み込み時に書き換える。その際に元の値を記録しておき、言語切替で即座に戻せるようにする。
 */

/** 書き換え 1 件の記録 */
interface Swap {
  /** 値を設定する関数（対象オブジェクトとキーを閉じ込めたもの） */
  readonly set: (value: string | undefined) => void;
  /** 元（英語）の値。元々存在しなかった場合は undefined */
  readonly en: string | undefined;
  /** 日本語の値 */
  readonly ja: string;
}

/** 記録の寿命（database: 起動中ずっと / map: マップ読み込みごとに入れ替え） */
export type SwapScope = 'database' | 'map';

/** 日本語表示が有効か（オプション画面で切り替える。既定は日本語） */
let japanese = true;

/** 書き換えの記録 */
const swaps: Record<SwapScope, Swap[]> = { database: [], map: [] };

/** 日本語表示が有効か */
export const isJapanese = (): boolean => japanese;

/** すべての記録を現在の言語に合わせて適用する */
const applyAll = (): void => {
  (Object.keys(swaps) as SwapScope[]).forEach((scope) => {
    swaps[scope].forEach((swap) => swap.set(japanese ? swap.ja : swap.en));
  });
};

/**
 * 表示言語を設定する。変化した場合だけ記録を適用する。
 * 表示中のウィンドウの再描画は、切り替えを行った画面（オプション画面）側で行う。
 */
export const setJapanese = (value: boolean): void => {
  if (value === japanese) {
    return;
  }
  japanese = value;
  applyAll();
};

/**
 * データを日本語に書き換え、元の値を記録する。
 * 現在の言語が英語なら記録だけ行い、値は変えない。
 *
 * @param scope 記録の寿命
 * @param en 元の値
 * @param ja 日本語の値
 * @param set 値を設定する関数
 */
export const registerSwap = (
  scope: SwapScope,
  en: string | undefined,
  ja: string,
  set: (value: string | undefined) => void,
): void => {
  swaps[scope].push({ set, en, ja });
  if (japanese) {
    set(ja);
  }
};

/** マップ単位の記録を破棄する（新しいマップの読み込み前に呼ぶ） */
export const clearMapSwaps = (): void => {
  swaps.map = [];
};

/** 記録件数（デバッグ表示用） */
export const swapCount = (): number => swaps.database.length + swaps.map.length;
