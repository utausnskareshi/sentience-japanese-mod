/**
 * 日本語向けの自動改行（禁則処理つき）モジュール。
 *
 * 英語版は手作業で改行されているため、日本語訳は表示時に幅を測って折り返す。
 * 幅の測り方（measure）と制御文字の展開方法（expandCode）は呼び出し側から注入する。
 * - ゲーム内: Canvas の measureText で実際のフォント幅を測る
 * - 作業用CLI: 全角 1・半角 0.55 などの概算で行数を見積もる
 *
 * Chromium 59 でも動くよう ES2017 までの機能だけを使う。
 */
import { ESCAPE_CODE_PATTERN } from './text.ts';

/** 折り返しの設定 */
export interface WrapOptions {
  /** 1 行の最大幅（measure と同じ単位） */
  readonly maxWidth: number;
  /** 表示文字列の幅を返す */
  readonly measure: (text: string) => number;
  /**
   * 制御文字が画面上で何の文字列になるかを返す（幅計算用）。
   * 色変更などの書式コードは空文字を返す。
   */
  readonly expandCode: (code: string) => string;
  /**
   * 行頭禁則文字の扱い（既定 true）。
   * - true: ぶら下げ（行末に最大 2 文字はみ出して置く）。はみ出しても表示できる窓（メッセージ窓など）向け。
   * - false: 追い出し（直前の文字ごと次の行へ送る）。maxWidth が描画できる幅そのものの窓（説明文など）向け。
   */
  readonly hanging?: boolean;
}

/** 折り返しの最小単位 */
type Atom =
  | { readonly type: 'code'; readonly raw: string; readonly display: string } // 制御文字
  | { readonly type: 'space'; readonly raw: string } // 空白（折り返し位置。折り返した行の先頭・末尾では捨てる）
  | { readonly type: 'word'; readonly raw: string } // 欧文の単語（途中で分割しない）
  | { readonly type: 'char'; readonly raw: string }; // 和文の 1 文字（前後で分割できる）

/** 行頭に置けない文字（行頭禁則） */
const LINE_START_FORBIDDEN = new Set(
  Array.from(
    '、。，．・：；？！‼⁇⁈⁉゛゜ヽヾゝゞ々〻ー－）］｝〕〉》」』】〙〗〟’”｠»' +
      'ぁぃぅぇぉっゃゅょゎゕゖァィゥェォッャュョヮヵヶ…‥〜～!?),.:;]}%',
  ),
);

/** 行末に置けない文字（行末禁則） */
const LINE_END_FORBIDDEN = new Set(Array.from('（［｛〔〈《「『【〘〖〝‘“｟«([{'));

/** ぶら下げを許す最大文字数（行頭禁則文字が連続した場合の上限） */
const MAX_HANGING = 2;

/** 欧文の単語を構成する文字か（ASCII とラテン文字。和文は 1 文字ずつ分割可能にする） */
const isWordChar = (ch: string): boolean => {
  const code = ch.charCodeAt(0);
  // 空白は単語に含めない
  if (ch === ' ' || ch === '\t') {
    return false;
  }
  // ASCII 可視文字・Latin-1 補助・ラテン文字拡張
  return (code >= 0x21 && code <= 0x7e) || (code >= 0xa0 && code <= 0x24f);
};

/** 制御文字を検出する正規表現（先頭一致用） */
const ESCAPE_HEAD_REGEX = new RegExp(`^${ESCAPE_CODE_PATTERN}`);

/**
 * 段落（改行を含まない文字列）を折り返し単位に分解する（純粋関数）。
 */
export const tokenize = (paragraph: string, expandCode: (code: string) => string): Atom[] => {
  const atoms: Atom[] = [];
  let index = 0;
  while (index < paragraph.length) {
    const rest = paragraph.slice(index);
    const codeMatch = ESCAPE_HEAD_REGEX.exec(rest);
    if (codeMatch !== null) {
      const raw = codeMatch[0];
      atoms.push({ type: 'code', raw, display: expandCode(raw) });
      index += raw.length;
      continue;
    }
    // サロゲートペアを壊さないよう、コードポイント単位で 1 文字取り出す
    const ch = String.fromCodePoint(rest.codePointAt(0) ?? 0);
    // 全角スペース（「？　」など）も空白として扱い、折り返した行の先頭・末尾では捨てる
    if (ch === ' ' || ch === '\t' || ch === '　') {
      atoms.push({ type: 'space', raw: ch });
      index += ch.length;
      continue;
    }
    if (isWordChar(ch)) {
      // 単語は「欧文文字が続く限り」まとめる（途中の制御文字で区切る）
      let end = index;
      while (end < paragraph.length) {
        const c = String.fromCodePoint(paragraph.codePointAt(end) ?? 0);
        if (c === '\\' || !isWordChar(c)) {
          break;
        }
        end += c.length;
      }
      // 先頭がバックスラッシュで制御文字にならなかった場合も 1 文字は進める
      const wordEnd = end === index ? index + ch.length : end;
      atoms.push({ type: 'word', raw: paragraph.slice(index, wordEnd) });
      index = wordEnd;
      continue;
    }
    atoms.push({ type: 'char', raw: ch });
    index += ch.length;
  }
  return atoms;
};

/** 折り返し単位の表示幅 */
const atomWidth = (atom: Atom, measure: (text: string) => number): number => {
  switch (atom.type) {
    case 'code':
      return atom.display === '' ? 0 : measure(atom.display);
    case 'space':
    case 'word':
    case 'char':
      return measure(atom.raw);
  }
};

/**
 * 画面に何かが表示される単位か。
 * 欧文の単語・和文の文字に加えて、名前（\N[n]）やアイコン（\I[n]）のように表示幅を持つ制御文字も含める
 * （色変更などの書式コードと空白は含めない）。表示される制御文字を数えないと、名前で始まる行の直後の空白が
 * 捨てられたり、名前だけの行から開き括弧を送れなかったりする（RA-04）。
 */
const isVisibleAtom = (atom: Atom): boolean =>
  atom.type === 'word' || atom.type === 'char' || (atom.type === 'code' && atom.display !== '');

/** 行に「目に見える内容」があるか（書式コードと空白だけなら false） */
const hasVisible = (line: readonly Atom[]): boolean => line.some(isVisibleAtom);

/**
 * 行に文字（欧文の単語・和文の文字）があるか（名前などの制御文字だけの行は false）。
 * 「幅に収まらなくても次の単位を同じ行に置くか」の判定だけに使う。名前だけの行には、収まらなくても
 * 次の 1 単位（「\N[1]は」の助詞など）を置き、名前と後ろの文字を別の行に分けない（従来どおりの挙動）。
 */
const hasText = (line: readonly Atom[]): boolean => line.some((a) => a.type === 'word' || a.type === 'char');

/** 行末禁則文字（開き括弧など）か */
const isLineEndForbidden = (atom: Atom): boolean => atom.type === 'char' && LINE_END_FORBIDDEN.has(atom.raw);

/** 行末の空白を取り除いた行を返す */
const trimTrailingSpaces = (line: readonly Atom[]): Atom[] => {
  const result = line.slice();
  while (result.length > 0 && result[result.length - 1]?.type === 'space') {
    result.pop();
  }
  return result;
};

/**
 * 行の末尾から次の行へ送るべき要素（直前の制御文字・行末禁則文字）を切り出す。
 *
 * @param line 行
 * @param includeDisplayCodes 表示幅のある制御文字（名前など）も送るか。
 *   文字の直前で改行する場合は true（「\N[1]は」の名前と助詞を同じ行に保つ）。
 *   空白で改行する場合は false（名前の後ろの空白は捨てられるので、名前を送ると次の語とくっついてしまう）
 */
const splitCarry = (line: readonly Atom[], includeDisplayCodes = true): { readonly keep: Atom[]; readonly carry: Atom[] } => {
  const keep = line.slice();
  const carry: Atom[] = [];
  // 次の単語の直前にある制御文字（色変更など）は次の行へ一緒に送る
  // 行末禁則文字（開き括弧など）も次の行へ送る
  for (;;) {
    const last = keep[keep.length - 1];
    if (last === undefined) {
      break;
    }
    const isCarryCode = last.type === 'code' && (includeDisplayCodes || last.display === '');
    const isForbiddenEnd = isLineEndForbidden(last);
    if (!isCarryCode && !isForbiddenEnd) {
      break;
    }
    // 送った結果、行に見える内容が無くなる場合は送らない（無限ループ防止）
    const candidate = keep.slice(0, -1);
    if (!hasVisible(candidate)) {
      break;
    }
    keep.pop();
    carry.unshift(last);
  }
  return { keep, carry };
};

/** 行頭禁則文字か */
const isLineStartForbidden = (atom: Atom): boolean => atom.type === 'char' && LINE_START_FORBIDDEN.has(atom.raw);

/**
 * 追い出し: 行末の「行頭禁則でない見える文字」を 1 つ（直前の制御文字ごと）次の行へ送る。
 * 送った結果、行に見える内容が無くなる場合は undefined（追い出せない）。
 */
const pushOut = (line: readonly Atom[]): { readonly keep: Atom[]; readonly carry: Atom[] } | undefined => {
  const keep = line.slice();
  const carry: Atom[] = [];
  // 行頭禁則文字（「。」「」」など）が続く間はまとめて送り、その前の普通の文字 1 つまでを送る
  for (;;) {
    const last = keep.pop();
    if (last === undefined) {
      return undefined;
    }
    carry.unshift(last);
    // 名前などの表示される制御文字も「普通の文字」として扱い、行頭禁則文字と一緒に送る
    if (isVisibleAtom(last) && !isLineStartForbidden(last)) {
      break;
    }
  }
  // 送る文字の直前にある制御文字（色変更など）も一緒に送る
  for (;;) {
    const last = keep[keep.length - 1];
    if (last === undefined || last.type !== 'code') {
      break;
    }
    keep.pop();
    carry.unshift(last);
  }
  if (!hasVisible(keep)) {
    return undefined;
  }
  // 残す行の末尾が行末禁則文字（開き括弧など）なら、それも次の行へ送る
  const split = splitCarry(trimTrailingSpaces(keep));
  return { keep: trimTrailingSpaces(split.keep), carry: split.carry.concat(carry) };
};

/**
 * 1 段落を折り返して行の配列にする（純粋関数）。
 * 段落の先頭の空白（センタリング用の字下げ）は保持し、折り返した 2 行目以降の先頭空白は捨てる。
 */
export const wrapParagraph = (paragraph: string, options: WrapOptions): string[] => {
  const atoms = tokenize(paragraph, options.expandCode);
  const widthCache = new Map<Atom, number>();
  const widthOf = (atom: Atom): number => {
    const cached = widthCache.get(atom);
    if (cached !== undefined) {
      return cached;
    }
    const w = atomWidth(atom, options.measure);
    widthCache.set(atom, w);
    return w;
  };
  const sum = (line: readonly Atom[]): number => line.reduce((acc, a) => acc + widthOf(a), 0);

  const lines: Atom[][] = [];
  let current: Atom[] = [];
  let width = 0;
  let hanging = 0;

  for (const atom of atoms) {
    const w = widthOf(atom);
    // 折り返し後の行頭の空白は捨てる（最初の行の字下げは残す）。
    // 名前などの表示される制御文字で始まる行の、その後ろの空白（「\N[1] said」の空白）は捨てない
    if (atom.type === 'space' && lines.length > 0 && !hasVisible(current)) {
      continue;
    }
    const fits = width + w <= options.maxWidth;
    if (fits || !hasText(current)) {
      current.push(atom);
      width += w;
      hanging = 0;
      continue;
    }
    if (isLineStartForbidden(atom)) {
      // 行頭禁則文字: ぶら下げ（行末にはみ出して置く）
      if (options.hanging !== false && hanging < MAX_HANGING) {
        current.push(atom);
        width += w;
        hanging += 1;
        continue;
      }
      // 追い出し（直前の文字ごと次の行へ送る）。ぶら下げない設定のときに加え、ぶら下げの上限（MAX_HANGING）を
      // 超えた場合も追い出す（そのまま改行すると 3 字目の行頭禁則文字が次の行の先頭に来る。RA-04）。
      // 追い出せない場合は下の通常処理で改行する
      const pushed = pushOut(current);
      if (pushed !== undefined) {
        lines.push(pushed.keep);
        current = pushed.carry.concat([atom]);
        width = sum(current);
        hanging = 0;
        continue;
      }
    }
    // 空白で溢れた場合は、空白を捨てて改行する。
    // ただし行末に開き括弧などの行末禁則文字が残る場合は、それ（と後ろの書式コード）を次の行へ送る（RA-04）。
    // 行末禁則文字が無いときは従来どおり何も送らない（書式コードの位置を変えない）
    if (atom.type === 'space') {
      const split = splitCarry(trimTrailingSpaces(current), false);
      const carry = split.carry.some(isLineEndForbidden) ? split.carry : [];
      lines.push(trimTrailingSpaces(carry.length > 0 ? split.keep : current));
      current = carry;
      width = sum(carry);
      hanging = 0;
      continue;
    }
    const { keep, carry } = splitCarry(trimTrailingSpaces(current));
    lines.push(trimTrailingSpaces(keep));
    current = carry.concat([atom]);
    width = sum(current);
    hanging = 0;
  }
  if (current.length > 0 || lines.length === 0) {
    lines.push(trimTrailingSpaces(current));
  }
  return lines.map((line) => line.map((a) => a.raw).join(''));
};

/**
 * 改行（\n）を含む文章を段落ごとに折り返す（純粋関数）。
 * 明示的な改行と空行はそのまま保持する。
 */
export const wrapText = (text: string, options: WrapOptions): string[] =>
  text.split('\n').reduce<string[]>((acc, paragraph) => acc.concat(wrapParagraph(paragraph, options)), []);

/**
 * 作業用CLI向けの概算幅（全角 = 1.0、それ以外 = 0.55）。
 * 実際のフォント幅とは異なるため、行数の目安を出す用途に限る。
 */
export const estimateWidth = (text: string): number =>
  Array.from(text).reduce((acc, ch) => acc + (isWordChar(ch) || ch === ' ' ? 0.55 : 1), 0);
