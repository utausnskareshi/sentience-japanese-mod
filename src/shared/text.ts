/**
 * 原文の正規化・分解ルールをまとめたモジュール。
 *
 * 抽出ツールとゲーム内プラグインが「同じ原文から同じキー」を作れるよう、
 * 正規化の規則はすべてここに集約する（両者から共通に import する）。
 * Chromium 59 でも動くよう、ES2017 までの機能だけを使うこと。
 */
import { textKey } from './hash.ts';

/** 訳文データの名前空間（用途ごとにキー空間を分ける） */
export type Namespace =
  | 'msg' // 「文章の表示」「文章のスクロール表示」のメッセージブロック
  | 'choice' // 選択肢
  | 'name' // アクター名・二つ名・プロフィール（セーブに保存されるため表示時に翻訳）
  | 'db' // アイテム・スキル・ステート等のデータベース文字列
  | 'term' // システム用語・プラグインのUI文言・ハードコードされた文言
  | 'map' // マップ表示名
  | 'quest' // クエスト名・説明・手順・カテゴリ
  | 'cem'; // コモンイベントメニュー（人物・生物データベース）の項目名と説明文

/** すべての名前空間（列挙・検証用） */
export const NAMESPACES: readonly Namespace[] = ['msg', 'choice', 'name', 'db', 'term', 'map', 'quest', 'cem'];

/** CRLF / CR を LF に統一する */
export const normalizeNewlines = (text: string): string => text.replace(/\r\n?/g, '\n');

/** 各行の行末の空白（半角スペース・タブ）を取り除く */
export const trimLineEnds = (text: string): string =>
  text
    .split('\n')
    .map((line) => line.replace(/[ \t]+$/, ''))
    .join('\n');

/** 末尾の空行を取り除く（先頭の空行はレイアウト用途があるため残す） */
const trimTrailingEmptyLines = (text: string): string => text.replace(/\n+$/, '');

/**
 * 複数行テキストの本文部分を正規化する。
 * 改行コード統一 → 行末空白除去 → 末尾空行除去 の順に適用する。
 */
export const normalizeBody = (text: string): string => trimTrailingEmptyLines(trimLineEnds(normalizeNewlines(text)));

// ---------------------------------------------------------------------------
// 制御文字（エスケープコード）
// ---------------------------------------------------------------------------

/**
 * 制御文字にマッチする正規表現の元パターン。
 * ツクールMV本体（Window_Base.obtainEscapeCode）と同じく、バックスラッシュ直後の
 * 英字は「すべて」コード名として扱う（例: "\fbHello" は FBHELLO という 1 つのコード）。
 * 引数は [..] または <..> 形式。記号 1 文字のコード（\{ \} \. \| \! \> \< \^ \$ \\）にも対応。
 */
export const ESCAPE_CODE_PATTERN = '\\\\(?:[A-Za-z]+(?:\\[[^\\]]*\\]|<[^>]*>)?|[{}.|!><^$\\\\])';

/** 文字列中の制御文字を順に取り出す（大文字小文字は正規化しない生の表記） */
export const listEscapeCodes = (text: string): string[] => text.match(new RegExp(ESCAPE_CODE_PATTERN, 'g')) ?? [];

/**
 * 制御文字を比較用に正規化する。
 * コード名は大文字化し、引数部分はそのまま残す（例: "\c[2]" → "\C[2]"）。
 */
export const normalizeEscapeCode = (code: string): string => {
  const match = /^\\([A-Za-z]+)(.*)$/.exec(code);
  if (match === null) {
    return code;
  }
  const name = match[1] ?? '';
  const rest = match[2] ?? '';
  return `\\${name.toUpperCase()}${rest}`;
};

// ---------------------------------------------------------------------------
// メッセージブロック（101 + 401...）
// ---------------------------------------------------------------------------

/** メッセージブロックを分解した結果 */
export interface MessageParts {
  /** 先頭の \pop[..] 群（吹き出しの表示先指定。訳文には含めず原文から引き継ぐ） */
  readonly prefix: string;
  /** 本文（正規化済み。翻訳キーの元になる） */
  readonly body: string;
}

/** 先頭の \pop[..] 群（GALV_MessageStyles の吹き出し指定）にマッチする */
const POP_PREFIX_REGEX = /^(?:\\pop\[[^\]]*\])+/i;

/**
 * 「文章の表示」の各行（401 の配列）を、吹き出し指定と本文に分解する。
 *
 * @param lines 401 コマンドの文字列（表示順）
 */
export const splitMessageLines = (lines: readonly string[]): MessageParts => {
  const joined = normalizeNewlines(lines.join('\n'));
  const match = POP_PREFIX_REGEX.exec(joined);
  const prefix = match === null ? '' : match[0];
  const body = normalizeBody(joined.slice(prefix.length));
  return { prefix, body };
};

/** メッセージブロックの翻訳キーを求める */
export const messageKey = (lines: readonly string[]): string => textKey(splitMessageLines(lines).body);

// ---------------------------------------------------------------------------
// 単一行の文字列（選択肢・用語・名前など）
// ---------------------------------------------------------------------------

/** 前後の飾り（空白や書式コード）と本文に分解した結果 */
export interface AffixedText {
  /** 先頭の空白・書式コード（訳文の前にそのまま付け直す） */
  readonly head: string;
  /** 本文（翻訳キーの元になる） */
  readonly body: string;
  /** 末尾の空白（訳文の後ろにそのまま付け直す） */
  readonly tail: string;
}

/**
 * 前後の空白だけを切り分ける（用語・アイテム名など）。
 * 本文内の改行は正規化する。
 */
export const splitWhitespace = (text: string): AffixedText => {
  const match = /^(\s*)([\s\S]*?)(\s*)$/.exec(normalizeNewlines(text));
  if (match === null) {
    return { head: '', body: text, tail: '' };
  }
  return { head: match[1] ?? '', body: trimLineEnds(match[2] ?? ''), tail: match[3] ?? '' };
};

/**
 * 選択肢用の分解。前後の空白に加えて、先頭の太字・斜体切替（\fb \fi）も飾りとして扱う。
 * 例: 「\fb (Nod quietly.) 」→ head「\fb 」/ body「(Nod quietly.)」/ tail「 」
 * ※ \fb の直後に英字が続く場合は別のコードになるため飾りとして扱わない。
 */
export const splitChoice = (text: string): AffixedText => {
  const match = /^((?:\s|\\f[bi](?![A-Za-z]))*)([\s\S]*?)(\s*)$/i.exec(normalizeNewlines(text));
  if (match === null) {
    return { head: '', body: text, tail: '' };
  }
  return { head: match[1] ?? '', body: trimLineEnds(match[2] ?? ''), tail: match[3] ?? '' };
};

/** 分解結果と訳文から表示用の文字列を組み立てる */
export const joinAffixed = (parts: AffixedText, translatedBody: string): string =>
  `${parts.head}${translatedBody}${parts.tail}`;

/** 単一行文字列（空白のみ分解）の翻訳キー */
export const plainKey = (text: string): string => textKey(splitWhitespace(text).body);

/** 選択肢の翻訳キー */
export const choiceKey = (text: string): string => textKey(splitChoice(text).body);

/**
 * 複数行ブロック（コモンイベントメニューの説明文など）の本文（翻訳キーの元）。
 * normalizeBody に加えて、先頭の空行も取り除く。抽出側と実行時側で必ずこの関数を使うこと。
 */
export const blockBody = (text: string): string => normalizeBody(text).replace(/^\n+/, '');

/** 複数行ブロックの翻訳キー */
export const blockKey = (text: string): string => textKey(blockBody(text));

/** 翻訳が不要な文字列か（空文字や空白のみ） */
export const isBlank = (text: string): boolean => text.trim().length === 0;
