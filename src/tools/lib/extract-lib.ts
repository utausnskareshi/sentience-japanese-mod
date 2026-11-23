/**
 * 抽出処理のうち、ゲームのプラグインと同じ解析を行う純粋関数群。
 *
 * ゲーム内プラグイン（実行時）が作るキーと一致させるため、使用中のプラグインの解析手順を
 * そのまま再現する。単体テストで確かめられるよう、ファイル入出力を持たない関数だけを置く。
 */

/**
 * プラグインコマンド「AddCommonEventMenu」の引数から、追加されるコモンイベントIDを求める。
 * YEP_CommonEventMenu（Game_Interpreter.prototype.addCommonEventMenu）と同じ規則:
 *   1. "x THROUGH y" / "x to y" なら x〜y の範囲（両端を含む）
 *   2. それ以外は、最初に現れる「数字のカンマ区切りの並び」に含まれる数字
 * なお本作のプラグイン構成には 1. で使う Yanfly.Util.getRange が無い（範囲指定はゲーム側でエラーになる）ため、
 * 実データに範囲指定は現れない。他の YEP プラグインでの定義どおり、両端を含む範囲として扱う。
 */
export const parseMenuIds = (args: string): number[] => {
  const range = /(\d+)[ ](?:THROUGH|to)[ ](\d+)/i.exec(args);
  if (range !== null) {
    const from = Number(range[1]);
    const to = Number(range[2]);
    return Array.from({ length: Math.max(0, to - from + 1) }, (_, i) => from + i);
  }
  const group = /(\d+(?:\s*,\s*\d+)*)/i.exec(args);
  return group === null ? [] : (group[1]?.match(/\d+/g) ?? []).map(Number);
};

/**
 * 注釈（108/408）の文字列を、YEP_CommonEventMenu の convertCommentsToText と同じ規則で行に分ける。
 * 各注釈の後ろに改行を付けて連結し、連続する改行（空行）を 1 つとして分割する。
 */
export const commentsToNotedata = (comments: readonly string[]): string[] =>
  comments
    .map((c) => `${c}\n`)
    .join('')
    .split(/[\r\n]+/);

/** コモンイベントメニューの注釈を解析した結果 */
export interface CommonEventMenuNotes {
  /** メニューに表示する項目名（<Menu Name: x> が無ければコモンイベント名） */
  readonly menuName: string;
  /** 説明文（<Subtext> が無ければ undefined）。YEP と同じく各行の後ろに改行が付く */
  readonly subtext: string | undefined;
}

/**
 * YEP_CommonEventMenu の processCEMNotetags1 と同じ順序で注釈を解析する（順序が違うとキーが一致しない）。
 *
 * @param notedata commentsToNotedata の結果
 * @param eventName コモンイベント名（既定の項目名）
 */
export const parseCommonEventMenuNotes = (notedata: readonly string[], eventName: string): CommonEventMenuNotes => {
  let menuName = eventName;
  let subtext: string | undefined;
  let mode: 'none' | 'subtext' | 'help' | 'enable' | 'visible' = 'none';
  for (const line of notedata) {
    const nameTag = /<MENU NAME:[ ](.*)>/i.exec(line);
    if (nameTag !== null) {
      menuName = nameTag[1] ?? '';
    } else if (/<ICON:[ ](\d+)>/i.test(line) || /<PICTURE:[ ](.*)>/i.test(line)) {
      // アイコン・画像の指定は説明文に含まれない
    } else if (/<HELP DESCRIPTION>/i.test(line)) {
      mode = 'help';
    } else if (/<\/HELP DESCRIPTION>/i.test(line)) {
      mode = 'none';
    } else if (mode === 'help') {
      // ヘルプ説明文（本作では非表示のため対象外）
    } else if (/<SUBTEXT>/i.test(line)) {
      mode = 'subtext';
      subtext = '';
    } else if (/<\/SUBTEXT>/i.test(line)) {
      mode = 'none';
    } else if (mode === 'subtext') {
      subtext = `${subtext ?? ''}${line}\n`;
    } else if (/<MENU ENABLE EVAL>/i.test(line)) {
      mode = 'enable';
    } else if (/<\/MENU ENABLE EVAL>/i.test(line)) {
      mode = 'none';
    } else if (mode === 'enable') {
      // 有効条件の式（翻訳対象外）
    } else if (/<MENU VISIBLE EVAL>/i.test(line)) {
      mode = 'visible';
    } else if (/<\/MENU VISIBLE EVAL>/i.test(line)) {
      mode = 'none';
    }
  }
  return { menuName, subtext };
};

/** 歩行グラフィック名から話者名らしい部分を取り出す（"$Dea%(8)" → "Dea"） */
export const cleanSpriteName = (name: string): string =>
  name
    .replace(/^[!$]+/, '')
    .replace(/%\(\d+\)/g, '')
    .replace(/_Pose$/i, '')
    .trim();

/** 自動生成風の二つ名（"X-12" など）は翻訳不要 */
export const isCodeLikeName = (text: string): boolean => /^[A-Z]{1,2}-\d+$/.test(text.trim());
