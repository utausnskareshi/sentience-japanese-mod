/**
 * TrueType フォントの cmap 表を読み、実際に字形（グリフ）がある文字（コードポイント）の集合を返す。
 * 訳文に、ゲーム同梱の日本語フォント（M+ 1m）に無い漢字が含まれていないかの確認に使う。
 *
 * 入力はゲーム側のファイル（ダウンロード途中・破損・改変の可能性がある）なので、表の位置・長さ・件数・区間をすべて検査し、
 * 不正なら「フォントの cmap 表が不正です（<理由>）: <パス>」の例外で止める。読み取りは表の一覧に書かれた cmap 表の長さと、
 * 各サブテーブルの長さの範囲に限る。区間の文字数の合計を先に確かめる（重ならない区間の合計は format 4 で 65,536、
 * format 12 で全 Unicode の 0x110000 を超えない）ため、細工したフォントでも処理する文字数はその範囲に収まる
 * （巨大な確保や長時間の空回りをしない）。
 */
import { readFileSync } from 'node:fs';

/** cmap のサブテーブル 1 件（エンコーディングレコード） */
interface CmapSubtable {
  readonly platformId: number;
  readonly encodingId: number;
  /** cmap 表の先頭からの位置 */
  readonly offset: number;
  readonly format: number;
}

/**
 * 使うサブテーブルの優先順位（先に見つかったもの 1 つだけを使う）。
 * ブラウザと同じく Windows の Unicode 全面（3,10）→ Windows の BMP（3,1）→ Unicode プラットフォーム（0,x）。
 * 複数を合算すると、ブラウザが使わない表にしか無い文字まで「ある」と判定してしまう。
 */
const SUBTABLE_PRIORITY: ReadonlyArray<(t: CmapSubtable) => boolean> = [
  (t) => t.platformId === 3 && t.encodingId === 10,
  (t) => t.platformId === 3 && t.encodingId === 1,
  (t) => t.platformId === 0,
];

/** 対応するサブテーブルの形式（4: BMP の区間表、12: 全面の区間表） */
const SUPPORTED_FORMATS: ReadonlySet<number> = new Set([4, 12]);

/** Unicode のコードポイントの最大値 */
const MAX_CODE_POINT = 0x10ffff;

/** format 4（BMP）の区間の文字数の合計の上限（重ならない区間なら BMP の 65,536 文字を超えない） */
const MAX_FORMAT4_SPAN = 0x10000;

/** format 12（全面）の区間の文字数の合計の上限（重ならない区間なら全 Unicode の 0x110000 文字を超えない） */
const MAX_FORMAT12_SPAN = MAX_CODE_POINT + 1;

/** 不正なフォントとして処理を止める関数（理由を受け取り、例外を投げる） */
type Fail = (reason: string) => never;

/** 範囲を限定した読み取り器（位置は範囲の先頭からの相対位置） */
interface Reader {
  /** 読み取れる範囲の長さ（バイト） */
  readonly length: number;
  readonly u16: (pos: number) => number;
  readonly i16: (pos: number) => number;
  readonly u32: (pos: number) => number;
  /** 4 文字の表名（tag）を読む */
  readonly tag: (pos: number) => string;
  /** 範囲の一部 [pos, pos + length) を、さらに範囲を限定した読み取り器にする（はみ出せば不正） */
  readonly sub: (pos: number, length: number, label: string) => Reader;
}

/**
 * バイト列の [start, start + length) だけを読む読み取り器を作る。
 * 範囲外の読み取りは DataView の RangeError ではなく、label と位置を含む「不正なフォント」として止める。
 */
const createReader = (view: DataView, start: number, length: number, label: string, fail: Fail): Reader => {
  /** 範囲内なら DataView 上の位置を返す */
  const at = (pos: number, size: number): number =>
    Number.isInteger(pos) && pos >= 0 && pos + size <= length ? start + pos : fail(`${label}の範囲外（位置 ${pos}、長さ ${length}）を読み取ろうとしました`);
  return {
    length,
    u16: (pos) => view.getUint16(at(pos, 2)),
    i16: (pos) => view.getInt16(at(pos, 2)),
    u32: (pos) => view.getUint32(at(pos, 4)),
    tag: (pos) => String.fromCharCode(...Array.from({ length: 4 }, (_, i) => view.getUint8(at(pos + i, 1)))),
    sub: (pos, subLength, subLabel) =>
      Number.isInteger(pos) && pos >= 0 && Number.isInteger(subLength) && subLength >= 0 && pos + subLength <= length
        ? createReader(view, start + pos, subLength, subLabel, fail)
        : fail(`${subLabel}（位置 ${pos}、長さ ${subLength}）が${label}（長さ ${length}）の外にはみ出しています`),
  };
};

/** フォントの表一覧から cmap 表を探し、その範囲だけを読む読み取り器を返す（無ければ undefined） */
const findCmapTable = (font: Reader, fail: Fail): Reader | undefined => {
  if (font.length < 12) {
    return fail(`ファイルが短すぎます（${font.length} バイト）`);
  }
  const numTables = font.u16(4);
  if (12 + numTables * 16 > font.length) {
    return fail(`表の一覧（${numTables} 件）がファイルに収まりません`);
  }
  const record = Array.from({ length: numTables }, (_, i) => 12 + i * 16).find((pos) => font.tag(pos) === 'cmap');
  return record === undefined ? undefined : font.sub(font.u32(record + 8), font.u32(record + 12), 'cmap 表');
};

/** cmap 表のエンコーディングレコード（サブテーブルの一覧）を読む */
const readSubtables = (cmap: Reader, fail: Fail): CmapSubtable[] => {
  const count = cmap.u16(2);
  if (4 + count * 8 > cmap.length) {
    return fail(`エンコーディングレコード（${count} 件）が cmap 表に収まりません`);
  }
  return Array.from({ length: count }, (_, i) => {
    const record = 4 + i * 8;
    const offset = cmap.u32(record + 4);
    return { platformId: cmap.u16(record), encodingId: cmap.u16(record + 2), offset, format: cmap.u16(offset) };
  });
};

/** 区間 1 つ（開始・終了は両端を含む） */
interface Range {
  readonly start: number;
  readonly end: number;
}

/**
 * 区間の文字数の合計が上限を超えないことを確かめる（超えれば不正なフォントとして止める）。
 * 区間が重なっていると、同じ文字を何度も数えることになり、細工したフォントで処理が膨れ上がるため。
 */
const assertTotalSpan = (ranges: readonly Range[], limit: number, label: string, fail: Fail): void => {
  const total = ranges.reduce((acc, r) => acc + (r.end - r.start + 1), 0);
  if (total > limit) {
    fail(`${label}の区間の文字数の合計（${total}）が上限（${limit}）を超えています（区間が重なっています）`);
  }
};

/**
 * format 4（BMP）で字形がある文字を集める。グリフ番号 0（.notdef = 字形なし）は除く。
 * サブテーブルの長さ（uint16）の範囲だけを読み、区間（segment）の「開始 ≤ 終了」と文字数の合計を先に確かめる。
 */
const collectFormat4 = (cmap: Reader, offset: number, out: Set<number>, fail: Fail): void => {
  const length = cmap.u16(offset + 2);
  if (length < 16) {
    fail(`format 4 の長さ（${length}）が短すぎます`);
  }
  const table = cmap.sub(offset, length, 'format 4 のサブテーブル');
  const segCountX2 = table.u16(6);
  if (segCountX2 === 0 || segCountX2 % 2 !== 0) {
    fail(`format 4 の区間の数（segCountX2 = ${segCountX2}）が不正です`);
  }
  const segCount = segCountX2 / 2;
  if (16 + segCount * 8 > length) {
    fail(`format 4 の区間の数（${segCount}）がサブテーブルの長さ（${length}）に収まりません`);
  }
  const endBase = 14;
  const startBase = endBase + segCount * 2 + 2;
  const deltaBase = startBase + segCount * 2;
  const rangeBase = deltaBase + segCount * 2;
  const segments = Array.from({ length: segCount }, (_, s): Range => {
    const end = table.u16(endBase + s * 2);
    const start = table.u16(startBase + s * 2);
    return start > end ? fail(`format 4 の区間 ${s} の開始（${start}）が終了（${end}）より後ろです`) : { start, end };
  });
  assertTotalSpan(segments, MAX_FORMAT4_SPAN, 'format 4 ', fail);
  segments.forEach(({ start, end }, s) => {
    const delta = table.i16(deltaBase + s * 2);
    const rangeOffsetPos = rangeBase + s * 2;
    const rangeOffset = table.u16(rangeOffsetPos);
    for (let c = start; c <= end && c !== 0xffff; c++) {
      let glyph: number;
      if (rangeOffset === 0) {
        glyph = (c + delta) & 0xffff;
      } else {
        // idRangeOffset は自分自身の位置からの相対位置で glyphIdArray を指す（仕様どおりの計算。範囲外は table が不正として止める）
        const raw = table.u16(rangeOffsetPos + rangeOffset + (c - start) * 2);
        glyph = raw === 0 ? 0 : (raw + delta) & 0xffff;
      }
      if (glyph !== 0) {
        out.add(c);
      }
    }
  });
};

/**
 * format 12（全面）で字形がある文字を集める。グリフ番号 0 は除く。
 * サブテーブルの長さ（uint32）の範囲だけを読み、区間（group）の「開始 ≤ 終了 ≤ 0x10FFFF」と文字数の合計を先に確かめる。
 */
const collectFormat12 = (cmap: Reader, offset: number, out: Set<number>, fail: Fail): void => {
  const length = cmap.u32(offset + 4);
  if (length < 16) {
    fail(`format 12 の長さ（${length}）が短すぎます`);
  }
  const table = cmap.sub(offset, length, 'format 12 のサブテーブル');
  const groupCount = table.u32(12);
  if (16 + groupCount * 12 > length) {
    fail(`format 12 の区間の数（${groupCount}）がサブテーブルの長さ（${length}）に収まりません`);
  }
  const groups = Array.from({ length: groupCount }, (_, g): Range & { readonly startGlyph: number } => {
    const base = 16 + g * 12;
    const start = table.u32(base);
    const end = table.u32(base + 4);
    return start > end || end > MAX_CODE_POINT
      ? fail(`format 12 の区間 ${g}（${start}〜${end}）が不正です（開始 ≤ 終了 ≤ 0x10FFFF であること）`)
      : { start, end, startGlyph: table.u32(base + 8) };
  });
  assertTotalSpan(groups, MAX_FORMAT12_SPAN, 'format 12 ', fail);
  groups.forEach(({ start, end, startGlyph }) => {
    for (let c = start; c <= end; c++) {
      if (startGlyph + (c - start) !== 0) {
        out.add(c);
      }
    }
  });
};

/**
 * フォントのデータ（バイト列）から、字形がある文字の集合を返す（純粋関数）。
 * cmap の format 4（BMP）と format 12（全面）に対応する。不正なフォントは where を含む例外で止める。
 *
 * @param where エラーメッセージに含める説明（ファイル名など）
 */
export const parseFontCodepoints = (data: Buffer, where: string): Set<number> => {
  const fail: Fail = (reason) => {
    throw new Error(`フォントの cmap 表が不正です（${reason}）: ${where}`);
  };
  try {
    const view = new DataView(data.buffer, data.byteOffset, data.byteLength);
    const cmap = findCmapTable(createReader(view, 0, data.byteLength, 'ファイル', fail), fail);
    if (cmap === undefined) {
      throw new Error(`フォントに cmap 表がありません: ${where}`);
    }
    const usable = readSubtables(cmap, fail).filter((t) => SUPPORTED_FORMATS.has(t.format));
    const chosen = SUBTABLE_PRIORITY.map((matches) => usable.find(matches)).find((t) => t !== undefined);
    if (chosen === undefined) {
      throw new Error(`フォントに対応する cmap サブテーブル（Unicode の format 4 / 12）がありません: ${where}`);
    }
    const codepoints = new Set<number>();
    if (chosen.format === 4) {
      collectFormat4(cmap, chosen.offset, codepoints, fail);
    } else {
      collectFormat12(cmap, chosen.offset, codepoints, fail);
    }
    return codepoints;
  } catch (error) {
    // 範囲の検査で防いでいるが、万一 DataView の範囲外になった場合も、原因とファイル名が分かる例外にする
    if (error instanceof RangeError) {
      throw new Error(`フォントの cmap 表が不正です（表の外を読み取ろうとしました: ${error.message}）: ${where}`, { cause: error });
    }
    throw error;
  }
};

/** フォントファイルの、字形がある文字の集合を返す */
export const readFontCodepoints = (path: string): Set<number> => parseFontCodepoints(readFileSync(path), path);
