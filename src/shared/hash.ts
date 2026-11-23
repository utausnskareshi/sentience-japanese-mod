/**
 * 原文テキストから翻訳キー（ハッシュ値）を作るモジュール。
 *
 * 配布する訳文データには原文を一切含めず、「原文のハッシュ値 → 訳文」の形で保持する。
 * そのため、抽出ツール（Node.js）とゲーム内プラグイン（Chromium 59）の双方で
 * 完全に同じ結果になるハッシュ関数が必要になる。
 *
 * - BigInt は Chromium 59 で使えないため、32bit 演算（Math.imul）だけで構成した
 *   53bit ハッシュ（cyrb53）を採用する。
 *   cyrb53 は bryc 氏が公開しているハッシュ関数（https://github.com/bryc/code ）で、
 *   作者はパブリックドメイン（必要なら MIT）として公開し、出典の表示を推奨している。ここではその計算方法に従って実装している。
 * - 文字列は UTF-16 コード単位で処理する（JavaScript の文字列表現そのまま）。
 * - 衝突確率は 1 万件規模で約 10^-8 と十分に小さい。
 */

/** 53bit ハッシュ値を 16 進 14 桁で表したときの桁数 */
export const HASH_HEX_LENGTH = 14;

/**
 * cyrb53 ハッシュを計算する（純粋関数）。
 *
 * @param text ハッシュ対象の文字列
 * @param seed シード値（既定 0。キー体系を変える場合のみ変更する）
 * @returns 0 以上 2^53 未満の整数
 */
export const cyrb53 = (text: string, seed = 0): number => {
  // 2 本の 32bit 状態を別々の乗数で撹拌する
  let h1 = 0xdeadbeef ^ seed;
  let h2 = 0x41c6ce57 ^ seed;
  for (let i = 0; i < text.length; i++) {
    const ch = text.charCodeAt(i);
    h1 = Math.imul(h1 ^ ch, 2654435761);
    h2 = Math.imul(h2 ^ ch, 1597334677);
  }
  // 最終撹拌（アバランシェ効果を高める）
  h1 = Math.imul(h1 ^ (h1 >>> 16), 2246822507);
  h1 ^= Math.imul(h2 ^ (h2 >>> 13), 3266489909);
  h2 = Math.imul(h2 ^ (h2 >>> 16), 2246822507);
  h2 ^= Math.imul(h1 ^ (h1 >>> 13), 3266489909);
  // 上位 21bit（h2）と下位 32bit（h1）を結合して 53bit にする
  return 4294967296 * (2097151 & h2) + (h1 >>> 0);
};

/**
 * 正規化済みの原文から翻訳キー（16 進 14 桁）を作る。
 *
 * @param normalizedSource 正規化済みの原文（正規化は text.ts の各関数で行う）
 * @returns 例: "0a1b2c3d4e5f60"
 */
export const textKey = (normalizedSource: string): string => {
  const hex = cyrb53(normalizedSource).toString(16);
  // padStart は ES2017 で利用可能
  return hex.padStart(HASH_HEX_LENGTH, '0');
};
