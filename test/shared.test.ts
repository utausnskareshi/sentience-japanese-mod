/**
 * 共通処理（ハッシュ・正規化・自動改行・バンドル検証）のテスト。
 */
import { describe, expect, it } from 'vitest';
import { cyrb53, textKey, HASH_HEX_LENGTH } from '../src/shared/hash.ts';
import {
  splitMessageLines,
  messageKey,
  splitChoice,
  splitWhitespace,
  joinAffixed,
  listEscapeCodes,
  normalizeEscapeCode,
  blockKey,
} from '../src/shared/text.ts';
import { wrapText, wrapParagraph, estimateWidth, type WrapOptions } from '../src/shared/wrap.ts';
import { parseBundle, overlaySpecsOf, BUNDLE_FORMAT, type OverlaySpec } from '../src/shared/bundle.ts';

describe('hash', () => {
  it('同じ入力から常に同じ値を返す', () => {
    expect(cyrb53('Hello')).toBe(cyrb53('Hello'));
    expect(textKey('Hello')).toBe(textKey('Hello'));
  });

  it('既知の値と一致する（実装変更でキー体系が壊れていないことの確認）', () => {
    // cyrb53 の公開実装による既知値
    expect(cyrb53('a')).toBe(7929297801672961);
    expect(cyrb53('revenge')).toBe(4051478007546757);
  });

  it('キーは 16 進 14 桁で、入力が違えば値も違う', () => {
    const a = textKey('Yes');
    const b = textKey('No');
    expect(a).toHaveLength(HASH_HEX_LENGTH);
    expect(a).toMatch(/^[0-9a-f]+$/);
    expect(a).not.toBe(b);
  });
});

describe('メッセージブロックの分解', () => {
  it('先頭の \\pop[n] を取り除き、行末空白を除去する', () => {
    const parts = splitMessageLines(['\\pop[6]The weather is fine, ', "and the tea is ", 'ready.']);
    expect(parts.prefix).toBe('\\pop[6]');
    expect(parts.body).toBe('The weather is fine,\nand the tea is\nready.');
  });

  it('吹き出し先が違っても本文が同じなら同じキーになる', () => {
    expect(messageKey(['\\pop[6]Hello.'])).toBe(messageKey(['\\pop[a1]Hello. ']));
  });

  it('先頭の空行と字下げは保持し、末尾の空行は除去する', () => {
    const parts = splitMessageLines(['', '    \\{\\{\\{            Day 2', '']);
    expect(parts.prefix).toBe('');
    expect(parts.body).toBe('\n    \\{\\{\\{            Day 2');
  });
});

describe('単一行文字列の分解', () => {
  it('選択肢の先頭の \\fb と前後の空白を飾りとして切り分ける', () => {
    const parts = splitChoice('\\fb (Nod quietly.) ');
    expect(parts).toEqual({ head: '\\fb ', body: '(Nod quietly.)', tail: ' ' });
    expect(joinAffixed(parts, '（静かにうなずく）')).toBe('\\fb （静かにうなずく） ');
  });

  it('前後の空白だけを切り分ける', () => {
    expect(splitWhitespace(' attacks!')).toEqual({ head: ' ', body: 'attacks!', tail: '' });
  });

  it('説明文ブロックは前後の空行と行末空白を無視してキー化する', () => {
    expect(blockKey('Line one \nLine two\n')).toBe(blockKey('\nLine one\nLine two'));
  });
});

describe('制御文字', () => {
  it('ツクールMVと同様に英字の連続をコード名として取り出す', () => {
    expect(listEscapeCodes('\\C[2]Hi\\C[0] \\n[3] \\fb \\{ \\pop[a1]')).toEqual([
      '\\C[2]',
      '\\C[0]',
      '\\n[3]',
      '\\fb',
      '\\{',
      '\\pop[a1]',
    ]);
  });

  it('比較用にコード名を大文字化する', () => {
    expect(normalizeEscapeCode('\\c[2]')).toBe('\\C[2]');
    expect(normalizeEscapeCode('\\{')).toBe('\\{');
  });
});

describe('自動改行', () => {
  /** 全角 1・半角 0.5 の簡易計測 */
  const options = (maxWidth: number): WrapOptions => ({
    maxWidth,
    measure: (text) => Array.from(text).reduce((acc, ch) => acc + (ch.charCodeAt(0) < 0x100 ? 0.5 : 1), 0),
    expandCode: (code) => (code.toLowerCase().startsWith('\\n[') ? 'アクター' : ''),
  });

  it('和文を指定幅で折り返す', () => {
    expect(wrapParagraph('あいうえおかきくけこ', options(4))).toEqual(['あいうえ', 'おかきく', 'けこ']);
  });

  it('行頭禁則文字はぶら下げる', () => {
    expect(wrapParagraph('あいうえ。かき', options(4))).toEqual(['あいうえ。', 'かき']);
  });

  it('行末禁則文字（開き括弧）は次の行へ送る', () => {
    expect(wrapParagraph('あいう「えお」', options(4))).toEqual(['あいう', '「えお」']);
  });

  it('欧文の単語は分割しない', () => {
    // 文字単位なら 1 行目に "X" が入る幅（4.5）だが、単語ごと次の行へ送られる
    expect(wrapParagraph('あいうえX-12です', options(4.5))).toEqual(['あいうえ', 'X-12です']);
  });

  it('単語直前の色変更コードは単語と一緒に次の行へ送る', () => {
    expect(wrapParagraph('あいう\\C[2]えお\\C[0]', options(3))).toEqual(['あいう', '\\C[2]えお\\C[0]']);
  });

  it('制御文字の展開後の幅で計算する', () => {
    // \n[1] は「アクター」（幅 4）として計算される
    expect(wrapParagraph('\\n[1]はい', options(5))).toEqual(['\\n[1]は', 'い']);
  });

  it('明示的な改行と空行を保持する', () => {
    expect(wrapText('あいう\n\nえお', options(10))).toEqual(['あいう', '', 'えお']);
  });

  it('追い出し方式（hanging: false）では行頭禁則文字を直前の文字ごと次の行へ送る', () => {
    const noHang = { ...options(4), hanging: false };
    expect(wrapParagraph('あいうえ。かき', noHang)).toEqual(['あいう', 'え。かき']);
    // 行頭禁則文字が連続する場合（「。」と「」」）も、その前の普通の文字ごと送る
    expect(wrapParagraph('あいうえ。」か', noHang)).toEqual(['あいう', 'え。」か']);
    // どの行も最大幅を超えない
    const lines = wrapParagraph('あいうえおかきくけこ、さしすせそ。たちつてと', noHang);
    lines.forEach((line) => expect(Array.from(line).length).toBeLessThanOrEqual(4));
  });

  it('追い出せない（行に 1 文字しかない）場合は通常の改行にする', () => {
    expect(wrapParagraph('あ。。。', { ...options(1), hanging: false })).toEqual(['あ', '。', '。', '。']);
  });

  it('既定（hanging 未指定）は従来どおりぶら下げる', () => {
    expect(wrapParagraph('あいうえ。かき', options(4))).toEqual(['あいうえ。', 'かき']);
  });

  it('折り返した行の先頭に全角スペースを残さない', () => {
    expect(wrapParagraph('あいう？　えおか', options(4))).toEqual(['あいう？', 'えおか']);
  });

  it('段落先頭の字下げは保持する', () => {
    expect(wrapText('    ２日目', options(10))).toEqual(['    ２日目']);
  });

  it('概算幅は全角 1・半角 0.55', () => {
    expect(estimateWidth('あa')).toBeCloseTo(1.55);
  });

  // ---- RA-04（禁則処理が漏れる経路）の回帰テスト ----

  it('空白で溢れたとき、行末に残る開き括弧は次の行へ送る', () => {
    // 修正前は ['あい「', 'えお']（行末に「が残る）
    expect(wrapParagraph('あい「 えお', options(3))).toEqual(['あい', '「えお']);
  });

  it('空白で溢れたとき、開き括弧が無ければ書式コードの位置は変えない（従来どおり）', () => {
    expect(wrapParagraph('あいう\\C[2] えお', options(3))).toEqual(['あいう\\C[2]', 'えお']);
  });

  it('文字で溢れたとき、空白の手前の開き括弧も次の行へ送る', () => {
    // 修正前は ['あ「', 'え']（行末の空白に隠れた「を送れなかった）
    expect(wrapParagraph('あ「　え', options(3))).toEqual(['あ', '「え']);
  });

  it('名前（表示される制御文字）で始まる行の、その後ろの空白を捨てない', () => {
    // 修正前は ['あいうえ', '\\n[1]said']（名前と次の語がくっつく）
    expect(wrapParagraph('あいうえ\\n[1] said', options(4))).toEqual(['あいうえ', '\\n[1] said']);
  });

  it('名前だけの行には、収まらなくても次の 1 文字を置く（名前と助詞を別の行に分けない）', () => {
    expect(wrapParagraph('\\n[1]はい', options(3))).toEqual(['\\n[1]は', 'い']);
  });

  it('ぶら下げの上限（2 字）を超える行頭禁則文字の連続は、直前の文字ごと追い出す', () => {
    // 修正前は ['あいうえ……', '」か']（3 字目の」が行頭に来る）
    expect(wrapParagraph('あいうえ……」か', options(4))).toEqual(['あいう', 'え……」', 'か']);
  });

  it('追い出し方式では、名前も普通の文字として行頭禁則文字と一緒に送る', () => {
    // 修正前は ['あ', 'い\\n[1]。']（名前を飛ばしてさらに前の文字まで送っていた）
    expect(wrapParagraph('あい\\n[1]。', { ...options(6), hanging: false })).toEqual(['あい', '\\n[1]。']);
  });
});

describe('重ね表示の定義の参照（RA-02）', () => {
  const overlays = parseBundle({
    format: BUNDLE_FORMAT,
    version: '1',
    entries: {},
    overlays: { pictures: { BaseMap: [{ x: 1, y: 2, width: 3, height: 4, text: '医療棟', fontSize: 17 }] } },
  }).overlays;

  it('表自身が持つ画像名の定義を返す', () => {
    expect(overlaySpecsOf(overlays.pictures, 'BaseMap')).toEqual([{ x: 1, y: 2, width: 3, height: 4, text: '医療棟', fontSize: 17 }]);
  });

  it('組み込みのプロパティ名（constructor・toString・__proto__ など）や未定義の名前には undefined を返す', () => {
    ['constructor', 'toString', 'valueOf', 'hasOwnProperty', '__proto__', 'Intro1', ''].forEach((name) => {
      expect(overlaySpecsOf(overlays.pictures, name)).toBeUndefined();
    });
  });

  it('配列でない値は定義として扱わない', () => {
    const broken = { Bad: 'not-an-array' } as unknown as Readonly<Record<string, readonly OverlaySpec[]>>;
    expect(overlaySpecsOf(broken, 'Bad')).toBeUndefined();
  });
});

describe('訳文バンドルの検証', () => {
  it('正しい形式を受け付け、欠けた名前空間は空にする', () => {
    const bundle = parseBundle({ format: BUNDLE_FORMAT, version: '1', entries: { msg: { abc: 'あ' } } });
    expect(bundle.entries.msg).toEqual({ abc: 'あ' });
    expect(bundle.entries.choice).toEqual({});
  });

  it('形式が不正なら理由つきで例外を投げる', () => {
    expect(() => parseBundle({ format: 'x' })).toThrow(/形式が不正/);
    expect(() => parseBundle({ format: BUNDLE_FORMAT, version: '1', entries: { msg: { a: 1 } } })).toThrow(
      /文字列ではありません/,
    );
  });
});
