/**
 * 作業用 CLI から切り出した判定（引数の解析・ビルドの検査）のテスト。
 * CLI 本体（ファイル入出力・外部プロセス）は薄く保ち、規則はここで確かめる。
 */
import { describe, expect, it } from 'vitest';
import { join, resolve } from 'node:path';
import { NAMESPACES, type Namespace } from '../src/shared/text.ts';
import { parseCliArgs } from '../src/tools/lib/cli.ts';
import { isAllowedHost, routeRequest } from '../src/tools/lib/devserver-lib.ts';
import { summarizeVerification } from '../src/tools/lib/verify-lib.ts';
import {
  EXE_MISMATCH_GUIDANCE,
  chooseZipTimestamp,
  compareSelfCheck,
  parseEpochSeconds,
  parseSelfCheckReport,
  sha256SumsLine,
} from '../src/tools/lib/package-lib.ts';
import {
  assertControlCodesMatch,
  findChromium59Incompatibilities,
  findControlCodeMismatches,
  formatProblemList,
  parseTranslationTable,
} from '../src/tools/lib/build-lib.ts';

describe('CLI の引数の解析（TA-12）', () => {
  const mergeSpec = {
    usage: 'npm run merge -- [--overwrite] [--force] [--dry-run] [--batch <ID> | ファイル...]',
    options: { overwrite: 'boolean', force: 'boolean', 'dry-run': 'boolean', batch: 'string' },
    allowPositionals: true,
  } as const;

  it('既知のオプション（値なし・値あり・--name=value）とファイル名を受け付ける', () => {
    const parsed = parseCliArgs(['--dry-run', '--batch', 'dlg-001'], mergeSpec);
    expect(parsed.values).toEqual({ overwrite: false, force: false, 'dry-run': true, batch: 'dlg-001' });
    expect(parsed.positionals).toEqual([]);
    const files = parseCliArgs(['--overwrite', 'work/out/a.json', 'work/out/b.json'], mergeSpec);
    expect(files.values.overwrite).toBe(true);
    expect(files.values.batch).toBeUndefined();
    expect(files.positionals).toEqual(['work/out/a.json', 'work/out/b.json']);
    expect(parseCliArgs(['--batch=dlg-002'], mergeSpec).values.batch).toBe('dlg-002');
    // "--" より後ろはすべてファイル名として扱う（"-" で始まるファイル名も渡せる）
    expect(parseCliArgs(['--', '--dry-run'], mergeSpec).positionals).toEqual(['--dry-run']);
    expect(parseCliArgs([], mergeSpec).values).toEqual({ overwrite: false, force: false, 'dry-run': false, batch: undefined });
  });

  it('未知のオプション（打ち間違い）は使い方つきで拒否する', () => {
    ['--dryrun', '--dry_run', '-n', '--constructor', '--toString'].forEach((option) =>
      expect(() => parseCliArgs([option, 'work/out/x.json'], mergeSpec), option).toThrow(
        new RegExp(`不明なオプションです: ${option}\\n使い方: npm run merge`),
      ),
    );
  });

  it('値を取るオプションの値が無い・空・オプションの書き忘れ・重複は拒否する', () => {
    expect(() => parseCliArgs(['--batch'], mergeSpec)).toThrow(/--batch の値がありません\n使い方:/);
    expect(() => parseCliArgs(['--batch', '--dry-run'], mergeSpec)).toThrow(/--batch の値がありません/);
    expect(() => parseCliArgs(['--batch='], mergeSpec)).toThrow(/--batch の値が空です/);
    expect(() => parseCliArgs(['--batch', 'a', '--batch', 'b'], mergeSpec)).toThrow(/--batch が複数回指定されています/);
    expect(() => parseCliArgs(['--force=yes'], mergeSpec)).toThrow(/--force は値を取りません/);
    // "-" で始まる値は --name=value の形なら受け付ける
    expect(parseCliArgs(['--batch=-x'], mergeSpec).values.batch).toBe('-x');
  });

  it('オプション以外の引数を受け付けない CLI に引数があれば拒否する', () => {
    const verifySpec = { usage: 'npm run verify -- [--game <フォルダ>] [--strict]', options: { game: 'string', strict: 'boolean' }, allowPositionals: false } as const;
    expect(parseCliArgs(['--game', 'D:\\Games\\Sentience', '--strict'], verifySpec).values).toEqual({ game: 'D:\\Games\\Sentience', strict: true });
    expect(() => parseCliArgs(['strict'], verifySpec)).toThrow(/不要な引数があります: strict/);
    expect(() => parseCliArgs(['--game'], verifySpec)).toThrow(/--game の値がありません/);
  });
});

describe('ビルドの訳文表の検証（TA-03・TA-04: CI でも制御文字・文字化けを止める）', () => {
  const key = '0123456789abcd';

  it('正しい訳文表はそのまま返す（改行は許す）', () => {
    expect(parseTranslationTable({ [key]: 'こんにちは。\n世界。' }, 'msg.json')).toEqual({ [key]: 'こんにちは。\n世界。' });
  });

  it('キーの形式・型・空の訳文は従来どおり拒否する', () => {
    expect(() => parseTranslationTable({ XYZ: 'あ' }, 'msg.json')).toThrow(/msg\.json: キーの形式が不正です: XYZ/);
    expect(() => parseTranslationTable({ [key]: 1 }, 'msg.json')).toThrow(/文字列を期待しました/);
    expect(() => parseTranslationTable({ [key]: '  ' }, 'msg.json')).toThrow(/訳文が空です/);
    expect(() => parseTranslationTable([], 'msg.json')).toThrow(/オブジェクトを期待しました/);
  });

  it('改行以外の C0 制御文字・U+FFFD を含む訳文があればキーつきで拒否する', () => {
    expect(() => parseTranslationTable({ [key]: '強調\x0cbです' }, 'msg.json')).toThrow(/msg\.json: 使えない文字を含む訳文が 1 件あります:\n {2}0123456789abcd: .*U\+000C/);
    expect(() => parseTranslationTable({ [key]: `回復${String.fromCharCode(0xfffd)}` }, 'db.json')).toThrow(/U\+FFFD/);
  });
});

describe('ビルドの制御文字の照合（TA-13: T-01 の照合を守る）', () => {
  /** 名前空間ごとの訳文表を作る（指定の無い名前空間は空） */
  const entriesOf = (partial: Partial<Record<Namespace, Record<string, string>>>): Record<Namespace, Record<string, string>> =>
    NAMESPACES.reduce(
      (acc, ns) => {
        acc[ns] = partial[ns] ?? {};
        return acc;
      },
      {} as Record<Namespace, Record<string, string>>,
    );
  const key = '0123456789abcd';

  it('原文の一覧と一致すれば通り、一覧に無いキーは「制御文字なし」として照合する', () => {
    const recorded = { [`msg:${key}`]: ['\\N[1]', '\\N[1]'] };
    expect(findControlCodeMismatches(entriesOf({ msg: { [key]: '\\N[1]と\\N[1]' }, choice: { [key]: 'はい' } }), recorded)).toEqual([]);
    expect(() => assertControlCodesMatch(entriesOf({ msg: { [key]: '\\N[1]と\\N[1]' } }), recorded)).not.toThrow();
  });

  it('個数の違い・余分な制御文字を検出し、ビルドを止める', () => {
    const recorded = { [`msg:${key}`]: ['\\N[1]', '\\N[1]'] };
    const entries = entriesOf({ msg: { [key]: '\\N[1]だけ' }, choice: { [key]: '\\C[2]はい' } });
    expect(findControlCodeMismatches(entries, recorded)).toEqual([`msg:${key}: \\N[1]（原文 2 / 訳文 1）`, `choice:${key}: \\C[2]（原文 0 / 訳文 1）`]);
    expect(() => assertControlCodesMatch(entries, recorded)).toThrow(/制御文字が原文と一致しない訳文が 2 件あります/);
  });

  it('一覧のファイルが無い場合は、訳文があれば止め、訳文が無ければ通す', () => {
    expect(() => assertControlCodesMatch(entriesOf({ msg: { [key]: 'あ' } }), undefined)).toThrow(/source-codes\.json がありません/);
    expect(() => assertControlCodesMatch(entriesOf({}), undefined)).not.toThrow();
  });

  it('一覧の形式が不正なら場所つきで例外を投げる', () => {
    expect(() => assertControlCodesMatch(entriesOf({ msg: { [key]: 'あ' } }), { [`msg:${key}`]: 'x' })).toThrow(/配列を期待しました/);
  });

  it('問題の一覧は表示上限で切り、残りの件数を示す', () => {
    expect(formatProblemList(['a', 'b', 'c'], 2)).toBe('  a\n  b\n  …ほか 1 件');
  });
});

describe('ゲーム内プラグインの出力の検査（TA-11: Chromium 59 で動かない書き方）', () => {
  it('現行の出力の形（wrap.ts の new RegExp・正規表現リテラル・先読み）は通す', () => {
    const current = [
      'var ESCAPE_CODE_PATTERN = "\\\\\\\\(?:[A-Za-z]+(?:\\\\[[^\\\\]]*\\\\]|<[^>]*>)?|[{}.|!><^$\\\\\\\\])";',
      'var ESCAPE_HEAD_REGEX = new RegExp("^".concat(ESCAPE_CODE_PATTERN));',
      'var POP_PREFIX_REGEX = /^(?:\\\\pop\\[[^\\]]*\\])+/i;',
      'var a = /(?=x)(?!y)\\d+/gu; var b = new RegExp(ESCAPE_CODE_PATTERN, "g");',
      'var global = window; var obj = { globalThis_like: 1 };',
    ].join('\n');
    expect(findChromium59Incompatibilities(current)).toEqual([]);
  });

  it('esbuild が new RegExp に変えて通す後読み・名前付きグループ・Unicode プロパティ・s/d/v フラグを検出する', () => {
    const code = [
      'const r1 = new RegExp("(?<=a)b");',
      'const r2 = new RegExp("(?<!a)b");',
      'const r3 = new RegExp("(?<y>\\\\d{4})");',
      'const r4 = new RegExp("\\\\p{Script=Han}", "u");',
      'const r5 = new RegExp("a.b", "s");',
      "const r6 = new RegExp('x', 'gd');",
      'const r7 = new RegExp(`[\\\\q{a}]`, "v");',
      'const r8 = /(?<=x)y/;',
    ].join('\n');
    const found = findChromium59Incompatibilities(code);
    expect(found).toEqual([
      expect.stringMatching(/^1 行目: 正規表現の後読み/),
      expect.stringMatching(/^2 行目: 正規表現の否定の後読み/),
      expect.stringMatching(/^3 行目: 正規表現の名前付きグループ/),
      expect.stringMatching(/^4 行目: 正規表現の Unicode プロパティ/),
      expect.stringMatching(/^5 行目: 正規表現のフラグ「s」/),
      expect.stringMatching(/^6 行目: 正規表現のフラグ「gd」/),
      expect.stringMatching(/^7 行目: 正規表現のフラグ「v」/),
      expect.stringMatching(/^8 行目: 正規表現の後読み/),
    ]);
  });

  it('Chromium 59 に無い API（globalThis・queueMicrotask・structuredClone など）を検出する', () => {
    const code = 'globalThis.x = 1;\nqueueMicrotask(f);\nconst c = structuredClone(o);\nnew AbortController();\nnew ResizeObserver(f);\nBigInt("1");';
    expect(findChromium59Incompatibilities(code)).toEqual([
      '1 行目: globalThis（Chromium 71 以降）',
      '2 行目: queueMicrotask（Chromium 71 以降）',
      '3 行目: structuredClone（Chromium 98 以降）',
      '4 行目: AbortController（Chromium 66 以降）',
      '5 行目: ResizeObserver（Chromium 64 以降）',
      '6 行目: BigInt（Chromium 67 以降）',
    ]);
  });
});

describe('開発用サーバの振り分けと Host の検査（TA-02・TA-13）', () => {
  const roots = { gameWww: resolve('game-www'), overlayWww: resolve('dist-www') };

  it('プラグインと訳文バンドルはビルド結果から、それ以外はゲームの www から返す', () => {
    expect(routeRequest('/', roots)).toEqual({ kind: 'file', path: join(roots.gameWww, 'index.html') });
    expect(routeRequest('/js/plugins.js?v=1', roots)).toEqual({ kind: 'plugins-js', path: join(roots.gameWww, 'js', 'plugins.js') });
    expect(routeRequest('/js/plugins/SentienceJP.js', roots)).toEqual({ kind: 'file', path: join(roots.overlayWww, 'js', 'plugins', 'SentienceJP.js') });
    expect(routeRequest('/sentience_jp/translation.json', roots)).toEqual({ kind: 'file', path: join(roots.overlayWww, 'sentience_jp', 'translation.json') });
    expect(routeRequest('/data/System.json', roots)).toEqual({ kind: 'file', path: join(roots.gameWww, 'data', 'System.json') });
    expect(routeRequest('/js/plugins/YEP_MessageCore.js', roots)).toEqual({ kind: 'file', path: join(roots.gameWww, 'js', 'plugins', 'YEP_MessageCore.js') });
  });

  it('フォルダ外への脱出は配信しない（path が undefined = 404）', () => {
    expect(routeRequest('/sentience_jp/../../secret.txt', roots)).toEqual({ kind: 'file', path: undefined });
    expect(routeRequest('/img/%2e%2e/%2e%2e/secret.txt', roots)).toEqual({ kind: 'file', path: undefined });
  });

  it('Host が 127.0.0.1:<port> / localhost:<port> のときだけ許可する（DNS リバインディング対策）', () => {
    expect(isAllowedHost('127.0.0.1:5635', 5635)).toBe(true);
    expect(isAllowedHost('localhost:5635', 5635)).toBe(true);
    expect(isAllowedHost('LocalHost:5635', 5635)).toBe(true);
    expect(isAllowedHost('attacker.example:5635', 5635)).toBe(false);
    expect(isAllowedHost('localhost:5699', 5635)).toBe(false);
    expect(isAllowedHost('localhost', 5635)).toBe(false);
    expect(isAllowedHost('127.0.0.1.attacker.example:5635', 5635)).toBe(false);
    expect(isAllowedHost(undefined, 5635)).toBe(false);
    // ポート 80 ではブラウザが Host にポートを付けないため、ポートなしも許可する
    expect(isAllowedHost('localhost', 80)).toBe(true);
    expect(isAllowedHost('127.0.0.1:80', 80)).toBe(true);
  });
});

describe('検証の終了コード（TA-13: verify --strict を守る）', () => {
  const none = { errors: 0, warnings: 0, missingGlyphKinds: 0, orphans: 0, untranslated: 0 };

  it('--strict で問題が 1 件でもあれば終了コード 1、問題が無ければ 0', () => {
    expect(summarizeVerification(none, { strict: true, allowUntranslated: false })).toEqual({ problemCount: 0, untranslatedProblem: 0, exitCode: 0 });
    (['errors', 'warnings', 'missingGlyphKinds', 'orphans'] as const).forEach((kind) =>
      expect(summarizeVerification({ ...none, [kind]: 1 }, { strict: true, allowUntranslated: false }).exitCode, kind).toBe(1),
    );
  });

  it('--strict でないときは、問題があっても終了コード 0（作業途中の確認用）', () => {
    expect(summarizeVerification({ ...none, errors: 3, untranslated: 5 }, { strict: false, allowUntranslated: false })).toEqual({
      problemCount: 3,
      untranslatedProblem: 0,
      exitCode: 0,
    });
  });

  it('--strict では未訳も問題として数え、--allow-untranslated で除外する', () => {
    expect(summarizeVerification({ ...none, untranslated: 5 }, { strict: true, allowUntranslated: false })).toEqual({ problemCount: 5, untranslatedProblem: 5, exitCode: 1 });
    expect(summarizeVerification({ ...none, untranslated: 5 }, { strict: true, allowUntranslated: true })).toEqual({ problemCount: 0, untranslatedProblem: 0, exitCode: 0 });
  });
});

describe('配布 Zip の作成の判定（TA-06・TA-07・TA-13）', () => {
  const manifestSha256 = 'a'.repeat(64);
  const okReport = [
    'OK: 埋め込みペイロードを検証しました（埋め込みリソース）。',
    'patchVersion=0.1.0 translationVersion=2026.11.23 files=2',
    `manifestSha256=${manifestSha256}`,
    '  www/js/plugins/SentienceJP.js (12345 bytes, sha256=...)',
  ];

  it('自己検査の結果ファイル（BOM・CRLF の有無を問わない）から版と manifest のハッシュを読む', () => {
    const expected = { ok: true, firstLine: okReport[0], patchVersion: '0.1.0', manifestSha256 };
    expect(parseSelfCheckReport(`${okReport.join('\n')}\n`)).toEqual(expected);
    expect(parseSelfCheckReport(`${String.fromCharCode(0xfeff)}${okReport.join('\r\n')}\r\n`)).toEqual(expected);
    // 失敗時（NG）と、manifestSha256 の行を出さない古い exe
    expect(parseSelfCheckReport('NG: 埋め込みペイロードがありません\r\n')).toEqual({
      ok: false,
      firstLine: 'NG: 埋め込みペイロードがありません',
      patchVersion: undefined,
      manifestSha256: undefined,
    });
    expect(parseSelfCheckReport(okReport.slice(0, 2).join('\n')).manifestSha256).toBeUndefined();
    // 大文字の 16 進・桁数違いは manifestSha256 の行として扱わない
    expect(parseSelfCheckReport(`manifestSha256=${'A'.repeat(64)}`).manifestSha256).toBeUndefined();
    expect(parseSelfCheckReport(`manifestSha256=${'a'.repeat(63)}`).manifestSha256).toBeUndefined();
  });

  it('版と manifest のハッシュが一致すれば通し、食い違い（古い exe・版の上げ忘れ・別のペイロード）を列挙する', () => {
    const expected = { version: '0.1.0', manifestSha256 };
    expect(compareSelfCheck(parseSelfCheckReport(okReport.join('\n')), expected)).toEqual([]);
    expect(compareSelfCheck(parseSelfCheckReport(okReport.join('\n')), { ...expected, version: '0.2.0' })).toEqual([
      'exe のパッチの版 0.1.0 が package.json の版 0.2.0 と異なります',
    ]);
    expect(compareSelfCheck(parseSelfCheckReport(okReport.join('\n')), { ...expected, manifestSha256: 'b'.repeat(64) })).toEqual([
      expect.stringMatching(/^exe に埋め込まれた manifest\.json（SHA-256 a{64}）が dist\/payload\/manifest\.json（b{64}）と異なります$/),
    ]);
    expect(compareSelfCheck(parseSelfCheckReport(okReport.slice(0, 2).join('\n')), expected)).toEqual([
      expect.stringMatching(/manifestSha256 の行がありません（.*古い exe です）/),
    ]);
    expect(compareSelfCheck(parseSelfCheckReport('NG: 壊れています'), expected)).toHaveLength(3);
    expect(EXE_MISMATCH_GUIDANCE).toBe('exe が現在のペイロード／版と一致しません。patcher\\build.ps1 で作り直してください');
  });

  it('更新日時は SOURCE_DATE_EPOCH → git のコミット時刻 → 現在時刻（警告つき）の順に決める', () => {
    const now = new Date(Date.UTC(2030, 0, 1));
    const gitFails = (): string => {
      throw new Error('fatal: not a git repository');
    };
    expect(chooseZipTimestamp('1763856000', () => '1700000000', () => now)).toEqual({
      date: new Date(1763856000 * 1000),
      source: 'SOURCE_DATE_EPOCH',
      warning: undefined,
    });
    expect(chooseZipTimestamp(undefined, () => '1763856000\n', () => now)).toEqual({
      date: new Date(1763856000 * 1000),
      source: 'git のコミット時刻',
      warning: undefined,
    });
    const fallback = chooseZipTimestamp(undefined, gitFails, () => now);
    expect(fallback.date).toBe(now);
    expect(fallback.source).toBe('現在時刻');
    expect(fallback.warning).toMatch(/git のコミット時刻を取得できなかったため、現在時刻を使います.*fatal: not a git repository/);
    // SOURCE_DATE_EPOCH が不正なら（git にも現在時刻にも頼らず）止める
    expect(() => chooseZipTimestamp('abc', () => '1700000000', () => now)).toThrow(/SOURCE_DATE_EPOCH は 0 以上の整数/);
  });

  it('UNIX 時刻は整数の秒で、Zip に記録できる範囲（1980〜2107 年）だけを受け付ける', () => {
    expect(parseEpochSeconds(' 315532800 ', 'X')).toEqual(new Date(Date.UTC(1980, 0, 1)));
    ['', '1.5', '-1', '1e9', '0x10', '１２３'].forEach((raw) => expect(() => parseEpochSeconds(raw, 'X'), raw).toThrow(/X は 0 以上の整数/));
    expect(() => parseEpochSeconds('0', 'X')).toThrow(/X が Zip に記録できる範囲（1980〜2107 年）の外です/);
    expect(() => parseEpochSeconds('4354819200', 'X')).toThrow(/範囲（1980〜2107 年）の外/);
  });

  it('SHA256SUMS.txt の行は「ハッシュ・空白 2 つ・ファイル名」（sha256sum -c の書式）', () => {
    expect(sha256SumsLine('0123abcd', 'SentienceJP-Patcher-v0.1.0.zip')).toBe('0123abcd  SentienceJP-Patcher-v0.1.0.zip');
  });
});
