/**
 * 作業用CLIから切り出した純粋関数（抽出の解析・訳文の統合規則・開発用サーバのパス解決・フォントの cmap 解析・
 * Steam の設定ファイル解析・識別子の分解）のテスト。
 */
import { describe, expect, it } from 'vitest';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { NAMESPACES, blockBody, blockKey } from '../src/shared/text.ts';
import { textKey } from '../src/shared/hash.ts';
import { commentsToNotedata, parseCommonEventMenuNotes, parseMenuIds } from '../src/tools/lib/extract-lib.ts';
import { mergeTranslations, normalizeTranslation } from '../src/tools/lib/merge-lib.ts';
import { resolveSafe } from '../src/tools/lib/paths.ts';
import { parseFontCodepoints, readFontCodepoints } from '../src/tools/lib/font.ts';
import { locateGame, readKeyValues } from '../src/tools/lib/game.ts';
import { parseUnitId, type TranslationTables } from '../src/tools/lib/translations.ts';
import { readSourceFile, type SourceUnit } from '../src/tools/lib/units.ts';

describe('AddCommonEventMenu の引数解析（YEP_CommonEventMenu と同じ規則）', () => {
  it('単一の ID とカンマ区切りの並び', () => {
    expect(parseMenuIds('5')).toEqual([5]);
    expect(parseMenuIds('1, 2,3')).toEqual([1, 2, 3]);
  });

  it('"x through y" / "x to y" は両端を含む範囲（大文字小文字を問わない）', () => {
    expect(parseMenuIds('3 through 5')).toEqual([3, 4, 5]);
    expect(parseMenuIds('3 THROUGH 5')).toEqual([3, 4, 5]);
    expect(parseMenuIds('3 to 5')).toEqual([3, 4, 5]);
  });

  it('最初に現れる数字の並びだけを使い、数字が無ければ空', () => {
    expect(parseMenuIds('menu 7, 8 extra 9')).toEqual([7, 8]);
    expect(parseMenuIds('abc')).toEqual([]);
  });
});

describe('コモンイベントメニューの注釈解析（YEP の processCEMNotetags1 と同じ順序）', () => {
  /** YEP と同じ手順で作った説明文（各行の後ろに改行） */
  const yepSubtext = (lines: readonly string[]): string => lines.map((l) => `${l}\n`).join('');

  it('注釈を連結して行に分け、空行は詰める', () => {
    expect(commentsToNotedata(['<Subtext>', 'Line one', '', 'Line two', '</Subtext>'])).toEqual([
      '<Subtext>',
      'Line one',
      'Line two',
      '</Subtext>',
      '',
    ]);
  });

  it('説明文と項目名を取り出し、キーが実行時（blockKey）と一致する', () => {
    const notes = parseCommonEventMenuNotes(
      commentsToNotedata(['<Menu Name: Dea>', '<Icon: 5>', '<Subtext>', 'An android.', 'Very kind.', '</Subtext>']),
      'CE Name',
    );
    expect(notes.menuName).toBe('Dea');
    expect(notes.subtext).toBe(yepSubtext(['An android.', 'Very kind.']));
    // 抽出側（blockBody → textKey）と実行時（blockKey）のキーが一致する
    expect(textKey(blockBody(notes.subtext ?? ''))).toBe(blockKey(yepSubtext(['An android.', 'Very kind.'])));
  });

  it('説明文の中の <Menu Name:> は項目名として扱い、説明文に含めない（YEP と同じ）', () => {
    const notes = parseCommonEventMenuNotes(commentsToNotedata(['<Subtext>', 'A', '<Menu Name: B>', 'C', '</Subtext>']), 'X');
    expect(notes.menuName).toBe('B');
    expect(notes.subtext).toBe(yepSubtext(['A', 'C']));
  });

  it('ヘルプ説明文の中の <Subtext> はヘルプ扱いで、説明文にならない（YEP と同じ）', () => {
    const notes = parseCommonEventMenuNotes(
      commentsToNotedata(['<Help Description>', '<Subtext>', 'help', '</Help Description>', '<Picture: face>']),
      'X',
    );
    expect(notes.subtext).toBeUndefined();
    expect(notes.menuName).toBe('X');
  });

  it('有効条件・表示条件の式は説明文に含めない', () => {
    const notes = parseCommonEventMenuNotes(
      commentsToNotedata(['<Menu Enable Eval>', 'enabled = true', '</Menu Enable Eval>', '<Subtext>', 'Body', '</Subtext>']),
      'X',
    );
    expect(notes.subtext).toBe(yepSubtext(['Body']));
  });
});

describe('訳文の統合規則', () => {
  const unit = (ns: SourceUnit['ns'], key: string, source: string): SourceUnit => ({
    ns,
    key,
    source,
    display: ns === 'choice' ? 'choice' : 'window',
    order: 0,
    count: 1,
    refs: [],
    scene: 's',
    sceneTitle: 's',
  });
  const emptyTables = (): TranslationTables =>
    NAMESPACES.reduce((acc, ns) => {
      acc[ns] = {};
      return acc;
    }, {} as TranslationTables);
  const units = [
    unit('msg', '00000000000001', 'Hello.'),
    unit('msg', '00000000000002', 'Hi \\C[2]there\\C[0].'),
    unit('choice', '00000000000003', 'Yes.'),
    unit('msg', '00000000000004', 'Good morning.'),
  ];
  const unitsById = new Map(units.map((u) => [`${u.ns}:${u.key}`, u]));
  const options = { overwrite: false, force: false };

  it('改行コード・行末空白を正規化し、選択肢だけ文末の「。」を除く', () => {
    expect(normalizeTranslation('msg', 'あ  \r\nい\rう')).toBe('あ\nい\nう');
    expect(normalizeTranslation('choice', 'はい。')).toBe('はい');
    expect(normalizeTranslation('msg', 'はい。')).toBe('はい。');
  });

  // TA-14: 原文の normalizeBody と同じく末尾の空行を取り除き、先頭の空行（見出しの字下げ）は残す
  it('末尾の空行を取り除き、先頭の空行は残す', () => {
    expect(normalizeTranslation('msg', 'こんにちは。\n')).toBe('こんにちは。');
    expect(normalizeTranslation('msg', 'こんにちは。\r\n\r\n  \n')).toBe('こんにちは。');
    expect(normalizeTranslation('msg', '\n2 日目\n\n\n')).toBe('\n2 日目');
    expect(normalizeTranslation('choice', 'はい。\n')).toBe('はい');
  });

  it('改行以外の制御文字・文字化けの痕跡・選択肢の改行を含む訳文は拒否する（TA-03・TA-04）', () => {
    const result = mergeTranslations(
      emptyTables(),
      {
        'msg:00000000000001': 'これは\x0cb強調\x0cbです。',
        'msg:00000000000004': `おは${String.fromCharCode(0xfffd)}よう。`,
        'choice:00000000000003': 'はい\nいいえ',
      },
      unitsById,
      options,
    );
    expect(result.added).toEqual([]);
    expect(result.rejected.map((r) => r.id).sort()).toEqual(['choice:00000000000003', 'msg:00000000000001', 'msg:00000000000004']);
  });

  it('新規は追加し、同一なら何もせず、相違は保留する（overwrite で上書き）', () => {
    const tables = emptyTables();
    tables.msg['00000000000001'] = 'こんにちは。';
    tables.msg['00000000000004'] = 'おはよう。';
    const output = { 'msg:00000000000001': 'やあ。', 'msg:00000000000004': 'おはよう。', 'choice:00000000000003': 'はい。' };
    const held = mergeTranslations(tables, output, unitsById, options);
    expect(held.added).toEqual(['choice:00000000000003']);
    expect(held.held).toEqual(['msg:00000000000001']);
    expect(held.changed).toEqual([]);
    expect(held.tables.msg['00000000000001']).toBe('こんにちは。');
    expect(held.tables.choice['00000000000003']).toBe('はい');
    const overwritten = mergeTranslations(tables, output, unitsById, { ...options, overwrite: true });
    expect(overwritten.changed).toEqual(['msg:00000000000001']);
    expect(overwritten.tables.msg['00000000000001']).toBe('やあ。');
    // 入力の表は変更しない
    expect(tables.msg['00000000000001']).toBe('こんにちは。');
    expect(tables.choice['00000000000003']).toBeUndefined();
  });

  it('未知の識別子・不正な形式・文字列以外・正規化後に空の訳文は拒否する', () => {
    const result = mergeTranslations(
      emptyTables(),
      { 'msg:ffffffffffffff': 'あ', 'bad-id': 'あ', 'msg:00000000000001': 3, 'choice:00000000000003': '。' },
      unitsById,
      options,
    );
    expect(result.rejected.map((r) => r.id).sort()).toEqual(['bad-id', 'choice:00000000000003', 'msg:00000000000001', 'msg:ffffffffffffff']);
    expect(result.added).toEqual([]);
  });

  it('制御文字が一致しない訳文は拒否し、force のときだけ取り込む', () => {
    const output = { 'msg:00000000000002': 'やあ。' };
    const rejected = mergeTranslations(emptyTables(), output, unitsById, options);
    expect(rejected.rejected.map((r) => r.id)).toEqual(['msg:00000000000002']);
    expect(rejected.tables.msg['00000000000002']).toBeUndefined();
    const forced = mergeTranslations(emptyTables(), output, unitsById, { ...options, force: true });
    expect(forced.added).toEqual(['msg:00000000000002']);
    expect(forced.issues.some((i) => i.issue.severity === 'error')).toBe(true);
  });

  it('警告（未翻訳の疑い）だけなら取り込み、指摘として返す', () => {
    const result = mergeTranslations(emptyTables(), { 'msg:00000000000001': 'Hello.' }, unitsById, options);
    expect(result.added).toEqual(['msg:00000000000001']);
    expect(result.issues.map((i) => i.issue.severity)).toEqual(['warning']);
  });
});

describe('開発用サーバのパス解決', () => {
  const base = resolve('test-www');

  it('通常のファイル要求は配信フォルダ内のパスになる', () => {
    expect(resolveSafe(base, '/img/a.png')).toBe(join(base, 'img', 'a.png'));
    expect(resolveSafe(base, '/img/a.png?ver=1')).toBe(join(base, 'img', 'a.png'));
    expect(resolveSafe(base, '/img/%E3%81%82.png')).toBe(join(base, 'img', 'あ.png'));
    expect(resolveSafe(base, '/./img/./a.png')).toBe(join(base, 'img', 'a.png'));
    expect(resolveSafe(base, '/')).toBe(base);
  });

  it('先頭の連続した "/" は無視する（UNC パスにならない）', () => {
    expect(resolveSafe(base, '//server/share/x')).toBe(join(base, 'server', 'share', 'x'));
  });

  it('フォルダ外への脱出を拒否する', () => {
    [
      '/../secret.txt',
      '/img/../../secret.txt',
      '/img/%2e%2e/%2E%2E/secret.txt',
      '/img/..%5c..%5csecret.txt',
      '/img\\..\\..\\secret.txt',
      '/C:/Windows/win.ini',
      '/C%3A/Windows/win.ini',
      '/index.html:stream',
      '/a%00b',
    ].forEach((path) => expect(resolveSafe(base, path), path).toBeUndefined());
  });

  it('不正なパーセントエンコードは拒否する', () => {
    expect(resolveSafe(base, '/%E0%A4%A')).toBeUndefined();
    expect(resolveSafe(base, '/%')).toBeUndefined();
  });
});

describe('フォントの cmap 解析', () => {
  /** cmap 表 1 つだけを持つ最小限の TrueType データを作る */
  const makeFont = (subtables: ReadonlyArray<{ readonly platformId: number; readonly encodingId: number; readonly body: Buffer }>): Buffer => {
    const headerSize = 4 + subtables.length * 8;
    const offsets = subtables.reduce<number[]>((acc, t, i) => [...acc, i === 0 ? headerSize : (acc[i - 1] ?? 0) + (subtables[i - 1]?.body.length ?? 0)], []);
    const cmapHeader = Buffer.alloc(headerSize);
    cmapHeader.writeUInt16BE(0, 0);
    cmapHeader.writeUInt16BE(subtables.length, 2);
    subtables.forEach((t, i) => {
      cmapHeader.writeUInt16BE(t.platformId, 4 + i * 8);
      cmapHeader.writeUInt16BE(t.encodingId, 4 + i * 8 + 2);
      cmapHeader.writeUInt32BE(offsets[i] ?? 0, 4 + i * 8 + 4);
    });
    const cmap = Buffer.concat([cmapHeader, ...subtables.map((t) => t.body)]);
    const directory = Buffer.alloc(12 + 16);
    directory.writeUInt32BE(0x00010000, 0);
    directory.writeUInt16BE(1, 4);
    directory.write('cmap', 12, 'latin1');
    directory.writeUInt32BE(28, 12 + 8);
    directory.writeUInt32BE(cmap.length, 12 + 12);
    return Buffer.concat([directory, cmap]);
  };

  /** format 4: A〜C（idDelta）、あ・ぃ・い（glyphIdArray。ぃ はグリフ 0）、ア（idDelta でグリフ 0）、終端 */
  const format4 = (): Buffer => {
    const segments = [
      { start: 0x41, end: 0x43, delta: -0x40, rangeOffset: 0 },
      { start: 0x3042, end: 0x3044, delta: 0, rangeOffset: 6 },
      { start: 0x30a2, end: 0x30a2, delta: -0x30a2, rangeOffset: 0 },
      { start: 0xffff, end: 0xffff, delta: 1, rangeOffset: 0 },
    ];
    const glyphIds = [5, 0, 7];
    const segCount = segments.length;
    const body = Buffer.alloc(16 + segCount * 8 + glyphIds.length * 2);
    body.writeUInt16BE(4, 0);
    body.writeUInt16BE(body.length, 2);
    body.writeUInt16BE(segCount * 2, 6);
    segments.forEach((s, i) => {
      body.writeUInt16BE(s.end, 14 + i * 2);
      body.writeUInt16BE(s.start, 16 + segCount * 2 + i * 2);
      body.writeInt16BE(s.delta, 16 + segCount * 4 + i * 2);
      body.writeUInt16BE(s.rangeOffset, 16 + segCount * 6 + i * 2);
    });
    glyphIds.forEach((g, i) => body.writeUInt16BE(g, 16 + segCount * 8 + i * 2));
    return body;
  };

  /** format 12: 𠀀〜𠀁（グリフ 10〜）と A（グリフ 0） */
  const format12 = (): Buffer => {
    const groups = [
      { start: 0x20000, end: 0x20001, glyph: 10 },
      { start: 0x41, end: 0x41, glyph: 0 },
    ];
    const body = Buffer.alloc(16 + groups.length * 12);
    body.writeUInt16BE(12, 0);
    body.writeUInt32BE(body.length, 4);
    body.writeUInt32BE(groups.length, 12);
    groups.forEach((g, i) => {
      body.writeUInt32BE(g.start, 16 + i * 12);
      body.writeUInt32BE(g.end, 16 + i * 12 + 4);
      body.writeUInt32BE(g.glyph, 16 + i * 12 + 8);
    });
    return body;
  };

  it('format 4 の idDelta と glyphIdArray を解釈し、グリフ 0 の文字を除く', () => {
    const codepoints = parseFontCodepoints(makeFont([{ platformId: 3, encodingId: 1, body: format4() }]), 'test');
    expect(Array.from(codepoints).sort((a, b) => a - b)).toEqual([0x41, 0x42, 0x43, 0x3042, 0x3044]);
  });

  it('(3,10) の表を優先し、他の表とは合算しない', () => {
    const font = makeFont([
      { platformId: 3, encodingId: 1, body: format4() },
      { platformId: 3, encodingId: 10, body: format12() },
    ]);
    expect(Array.from(parseFontCodepoints(font, 'test')).sort((a, b) => a - b)).toEqual([0x20000, 0x20001]);
  });

  it('Unicode の表が無ければ理由つきで例外を投げる', () => {
    expect(() => parseFontCodepoints(makeFont([{ platformId: 1, encodingId: 0, body: format4() }]), 'mac.ttf')).toThrow(/mac\.ttf/);
  });

  /** idDelta だけで字形を割り当てる format 4（区間ごとに開始・終了・idDelta・idRangeOffset を指定） */
  const format4Of = (segments: ReadonlyArray<{ readonly start: number; readonly end: number; readonly delta: number; readonly rangeOffset?: number }>): Buffer => {
    const segCount = segments.length;
    const body = Buffer.alloc(16 + segCount * 8);
    body.writeUInt16BE(4, 0);
    body.writeUInt16BE(body.length, 2);
    body.writeUInt16BE(segCount * 2, 6);
    segments.forEach((s, i) => {
      body.writeUInt16BE(s.end, 14 + i * 2);
      body.writeUInt16BE(s.start, 16 + segCount * 2 + i * 2);
      body.writeInt16BE(s.delta, 16 + segCount * 4 + i * 2);
      body.writeUInt16BE(s.rangeOffset ?? 0, 16 + segCount * 6 + i * 2);
    });
    return body;
  };
  const sorted = (set: ReadonlySet<number>): number[] => Array.from(set).sort((a, b) => a - b);

  // TA-13（K-03 を守る）: (3,1) は (0,x) より優先し、合算しない
  it('(3,1) の表を (0,3) の表より優先する（並び順に依らない）', () => {
    const unicode = { platformId: 0, encodingId: 3, body: format4Of([{ start: 0x41, end: 0x42, delta: 1 }, { start: 0xffff, end: 0xffff, delta: 1 }]) };
    const windows = { platformId: 3, encodingId: 1, body: format4Of([{ start: 0x3042, end: 0x3042, delta: 1 }, { start: 0xffff, end: 0xffff, delta: 1 }]) };
    expect(sorted(parseFontCodepoints(makeFont([unicode, windows]), 'test'))).toEqual([0x3042]);
    expect(sorted(parseFontCodepoints(makeFont([windows, unicode]), 'test'))).toEqual([0x3042]);
    // (3,x) が無ければ (0,x) を使う
    expect(sorted(parseFontCodepoints(makeFont([unicode]), 'test'))).toEqual([0x41, 0x42]);
  });

  it('format 4 の終端の区間（0xFFFF）は字形があっても文字として数えない', () => {
    // 0xFFFF の idDelta 0 はグリフ 0xFFFF（0 ではない）になるが、終端の目印なので除く
    const font = makeFont([{ platformId: 3, encodingId: 1, body: format4Of([{ start: 0x41, end: 0x41, delta: 1 }, { start: 0xfffe, end: 0xffff, delta: 0 }]) }]);
    expect(sorted(parseFontCodepoints(font, 'test'))).toEqual([0x41, 0xfffe]);
  });

  // TA-05: 壊れた・細工したフォントは、原因とファイル名を含む例外で即座に止める（巨大な確保・空回りをしない）
  it('壊れたフォント（短い・表の位置や長さがファイルの外）は理由とファイル名を含む例外にする', () => {
    const valid = makeFont([{ platformId: 3, encodingId: 1, body: format4() }]);
    expect(() => parseFontCodepoints(Buffer.alloc(0), 'broken.ttf')).toThrow(/^フォントの cmap 表が不正です（ファイルが短すぎます（0 バイト））: broken\.ttf$/);
    expect(() => parseFontCodepoints(valid.subarray(0, 20), 'broken.ttf')).toThrow(/表の一覧（1 件）がファイルに収まりません.*broken\.ttf/);
    expect(() => parseFontCodepoints(valid.subarray(0, 40), 'broken.ttf')).toThrow(/cmap 表（位置 28、長さ \d+）がファイル（長さ 40）の外にはみ出しています.*broken\.ttf/);
    // サブテーブルの位置が cmap 表の外
    const outside = Buffer.from(valid);
    outside.writeUInt32BE(0x7fffffff, 28 + 4 + 4);
    expect(() => parseFontCodepoints(outside, 'broken.ttf')).toThrow(/cmap 表の範囲外.*を読み取ろうとしました.*broken\.ttf/);
  });

  it('format 12 の区間の数・区間（開始 ≤ 終了 ≤ 0x10FFFF）・文字数の合計を検査する', () => {
    const f12 = (groups: ReadonlyArray<readonly [number, number, number]>, declared = groups.length): Buffer => {
      const body = Buffer.alloc(16 + groups.length * 12);
      body.writeUInt16BE(12, 0);
      body.writeUInt32BE(body.length, 4);
      body.writeUInt32BE(declared, 12);
      groups.forEach(([start, end, glyph], i) => {
        body.writeUInt32BE(start, 16 + i * 12);
        body.writeUInt32BE(end, 20 + i * 12);
        body.writeUInt32BE(glyph, 24 + i * 12);
      });
      return body;
    };
    const fontOf = (body: Buffer): Buffer => makeFont([{ platformId: 3, encodingId: 10, body }]);
    expect(() => parseFontCodepoints(fontOf(f12([[0, 0xffffffff, 1]])), 'f.ttf')).toThrow(/区間 0（0〜4294967295）が不正です.*f\.ttf/);
    expect(() => parseFontCodepoints(fontOf(f12([[0x42, 0x41, 1]])), 'f.ttf')).toThrow(/区間 0（66〜65）が不正です/);
    expect(() => parseFontCodepoints(fontOf(f12([[0x41, 0x41, 1]], 0xffffffff)), 'f.ttf')).toThrow(/format 12 の区間の数（4294967295）がサブテーブルの長さ（28）に収まりません/);
    // 重なった区間で文字数の合計が全 Unicode を超えるもの（全面の区間を 2 つ）
    expect(() => parseFontCodepoints(fontOf(f12([[0, 0x10ffff, 1], [0, 0x10ffff, 1]])), 'f.ttf')).toThrow(/文字数の合計（2228224）が上限（1114112）を超えています/);
  });

  it('format 4 の区間（開始 ≤ 終了）・文字数の合計・glyphIdArray の位置を検査する', () => {
    const fontOf = (body: Buffer): Buffer => makeFont([{ platformId: 3, encodingId: 1, body }]);
    expect(() => parseFontCodepoints(fontOf(format4Of([{ start: 0x42, end: 0x41, delta: 1 }])), 'f.ttf')).toThrow(/format 4 の区間 0 の開始（66）が終了（65）より後ろです/);
    // 重なった区間で文字数の合計が BMP（65,536）を超えるもの（findings では 33 秒空回りした形）
    const overlapping = format4Of(Array.from({ length: 2000 }, () => ({ start: 0, end: 0xfffe, delta: 1 })));
    expect(() => parseFontCodepoints(fontOf(overlapping), 'f.ttf')).toThrow(/format 4 の区間の文字数の合計（131070000）が上限（65536）を超えています/);
    // idRangeOffset がサブテーブルの外を指す
    expect(() => parseFontCodepoints(fontOf(format4Of([{ start: 0x41, end: 0x41, delta: 0, rangeOffset: 0x7ffe }, { start: 0xffff, end: 0xffff, delta: 1 }])), 'f.ttf')).toThrow(
      /format 4 のサブテーブルの範囲外.*を読み取ろうとしました.*f\.ttf/,
    );
  });

  // TA-05: 実フォントの字数を固定する（ゲームが見つからない環境＝CI では省略）
  it('実際のゲームのフォント（www/fonts/mplus-1m-regular.ttf）の収録字数は 7,508 字', (context) => {
    const www = ((): string | undefined => {
      try {
        return locateGame(undefined).www;
      } catch {
        // ゲームがインストールされていない環境では、このテストを省略する（下の context.skip）
        return undefined;
      }
    })();
    if (www === undefined) {
      context.skip('ゲームのインストール先が見つからないため省略');
      return;
    }
    expect(readFontCodepoints(join(www, 'fonts', 'mplus-1m-regular.ttf')).size).toBe(7508);
  });
});

describe('Steam の設定ファイル（KeyValues）と識別子', () => {
  it('"key" "value" をすべて取り出し、\\\\ を \\ に戻す', () => {
    const vdf = '"libraryfolders"\n{\n\t"0"\n\t{\n\t\t"path"\t\t"C:\\\\Program Files (x86)\\\\Steam"\n\t}\n\t"1"\n\t{\n\t\t"path"\t\t"D:\\\\SteamLibrary"\n\t}\n}';
    expect(readKeyValues(vdf, 'path')).toEqual(['C:\\Program Files (x86)\\Steam', 'D:\\SteamLibrary']);
    expect(readKeyValues(vdf, 'missing')).toEqual([]);
  });

  it('識別子を名前空間とキーに分解し、不正な形式は undefined', () => {
    expect(parseUnitId('msg:0a1b2c3d4e5f60')).toEqual({ ns: 'msg', key: '0a1b2c3d4e5f60' });
    expect(parseUnitId('unknown:0a1b')).toBeUndefined();
    expect(parseUnitId('msg:XYZ')).toBeUndefined();
    expect(parseUnitId('msg0a1b')).toBeUndefined();
  });
});

describe('抽出結果ファイルの読み込み', () => {
  /** 一時フォルダに JSON を書いて readSourceFile で読む（テスト用の架空のデータのみ） */
  const readFrom = (value: unknown): ReturnType<typeof readSourceFile> => {
    const dir = mkdtempSync(join(tmpdir(), 'sentience-jp-test-'));
    try {
      const path = join(dir, 'units.json');
      writeFileSync(path, JSON.stringify(value), 'utf8');
      return readSourceFile(path);
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  };
  const validUnit = {
    ns: 'msg',
    key: '00000000000001',
    source: 'Hello.',
    display: 'pop',
    order: 1,
    count: 1,
    refs: [{ file: 'Map001.json', where: 'EV1 p0 #0' }],
    scene: 'Map001.json#EV1',
    sceneTitle: 'Map001 / EV001',
  };

  it('正しい形式なら読み込み、話者は任意項目として扱う', () => {
    const file = readFrom({ generatedAt: '2026-11-23T00:00:00Z', buildId: '1', units: [validUnit, { ...validUnit, key: '00000000000002', speaker: 'Dea' }] });
    expect(file.units).toHaveLength(2);
    expect(file.units[0]?.speaker).toBeUndefined();
    expect(file.units[1]?.speaker).toBe('Dea');
  });

  it('不明な名前空間・表示種別、型違いは場所つきで例外を投げる', () => {
    expect(() => readFrom({ generatedAt: 'x', units: [{ ...validUnit, ns: 'bad' }] })).toThrow(/units\[0\]\.ns.*不明な名前空間/);
    expect(() => readFrom({ generatedAt: 'x', units: [{ ...validUnit, display: 'bad' }] })).toThrow(/units\[0\]\.display.*不明な表示種別/);
    expect(() => readFrom({ generatedAt: 'x', units: [{ ...validUnit, count: '1' }] })).toThrow(/units\[0\]\.count.*数値/);
    expect(() => readFrom({ generatedAt: 'x', units: {} })).toThrow(/units.*配列/);
  });

  // TA-13: 場面の見出し・出現箇所の型も検査する（翻訳者向けの台本が "undefined" などで崩れないように）
  it('場面の見出し（sceneTitle）・出現箇所（refs）の型違いも場所つきで例外を投げる', () => {
    expect(() => readFrom({ generatedAt: 'x', units: [{ ...validUnit, sceneTitle: 1 }] })).toThrow(/units\[0\]\.sceneTitle.*文字列/);
    expect(() => readFrom({ generatedAt: 'x', units: [{ ...validUnit, refs: [{ file: 'Map001.json' }] }] })).toThrow(/units\[0\]\.refs\[0\]\.where.*文字列/);
  });
});

describe('ゲームの場所（--game の指定）', () => {
  /** 一時フォルダにゲームの必須ファイル（中身は空）を作る（架空のフォルダ） */
  const withFakeGame = (files: readonly string[], test: (root: string) => void): void => {
    const root = mkdtempSync(join(tmpdir(), 'sentience-jp-game-'));
    try {
      files.forEach((file) => {
        mkdirSync(dirname(join(root, file)), { recursive: true });
        writeFileSync(join(root, file), '');
      });
      test(root);
    } finally {
      rmSync(root, { recursive: true, force: true });
    }
  };
  const required = ['nw.exe', 'www/data/System.json', 'www/js/plugins.js'];

  // TA-13: --game 指定時の www の場所（ゲームのルートの www）を守る
  it('指定したフォルダをゲームのルートとし、www はその直下の www', () => {
    withFakeGame(required, (root) => {
      expect(locateGame(root)).toEqual({ root, www: join(root, 'www'), buildId: undefined });
    });
  });

  it('必須ファイルが揃っていなければ、指定されたフォルダを示して例外を投げる', () => {
    withFakeGame(['nw.exe', 'www/js/plugins.js'], (root) => {
      expect(() => locateGame(root)).toThrow(/指定されたフォルダは Sentience のインストール先ではありません/);
    });
  });
});
