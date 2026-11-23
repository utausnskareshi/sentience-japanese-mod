/**
 * 画像内の英語（イントロの年号・見出し、エリアマップのラベルなど）の日本語化。
 *
 * 画像ファイルは復号も書き換えもしない。ゲームが通常どおり表示した画像の上に、
 * 英語の部分を隠す背景と日本語の文字を描く。
 * - ピクチャ・遠景・タイトル画面: 背景と文字を描いたスプライトを「子」として重ねる。
 *   子スプライトなので、元の画像の移動・拡大・フェードにそのまま追従する。
 * - YEP_CommonEventMenu のピクチャ窓（エリアマップ）: 画像が窓の contents へ縮小して描かれるため、
 *   同じ縮小率で contents に直接描く（drawCommonEventMenuPictureOverlay）。
 */
import { overlaySpecsOf, type OverlaySpec } from '../../shared/bundle.ts';
import { getPrototype, isRecord, reportError, wrapMethod, type Method } from '../env.ts';
import { bundleOverlays } from '../translator.ts';
import { isJapanese } from '../state.ts';

/** 管理用プロパティ（他プラグインと衝突しない名前にする） */
const OVERLAY_KEY = '__sentienceJpOverlayKey';
const OVERLAY_SPRITE = '__sentienceJpOverlay';
const OVERLAY_TARGET = '__sentienceJpOverlayTarget';
/** 最後に報告した失敗の内容（毎フレームの処理で同じ失敗を繰り返し報告しないため） */
const OVERLAY_ERROR = '__sentienceJpOverlayError';

/** 文字を縮める下限（これより小さくはしない） */
const MIN_FONT_SIZE = 10;

/** 縁取りの太さ（ツクールMVの Bitmap の既定値。縮小して描くときは縮尺を掛ける） */
const OUTLINE_WIDTH = 4;

/** 描画先のビットマップ上の矩形（整数座標） */
interface Rect {
  readonly x: number;
  readonly y: number;
  readonly width: number;
  readonly height: number;
}

/**
 * 1 行の文字列が幅に収まる文字サイズを求める（収まらなければ下限まで 1px ずつ縮める）。
 * drawText の maxWidth による横方向の圧縮（字形が潰れる）を避けるため。
 */
const fitFontSize = (bitmap: Bitmap, text: string, maxWidth: number, initialSize: number): number => {
  let size = initialSize;
  bitmap.fontSize = size;
  while (size > MIN_FONT_SIZE && bitmap.measureTextWidth(text) > maxWidth) {
    size -= 1;
    bitmap.fontSize = size;
  }
  return size;
};

/**
 * 定義 1 件（英語を隠す背景と日本語の文字）を、描画先のビットマップの指定した矩形に描く。
 * - スプライトとして重ねる場合: 定義と同じ大きさの新しいビットマップに、原点・等倍（scale = 1）で描く
 *   （等倍のときの結果は、この関数を切り出す前の描画と同じになるようにしている）
 * - 窓の contents に直接描く場合: 画像の縮小率に合わせて縮めた矩形・文字サイズ・縁取りで描く（RA-01）
 *
 * @param bitmap 描画先
 * @param spec 重ね表示の定義
 * @param rect 描画先での矩形（背景を塗り、文字を置く範囲）
 * @param scale 文字サイズ・縁取り・余白に掛ける縮尺（等倍は 1）
 */
const drawOverlaySpec = (bitmap: Bitmap, spec: OverlaySpec, rect: Rect, scale: number): void => {
  if (spec.fill !== undefined) {
    bitmap.fillRect(rect.x, rect.y, rect.width, rect.height, spec.fill);
  }
  bitmap.fontFace = 'GameFont, SentienceJP';
  bitmap.textColor = spec.color ?? '#ffffff';
  bitmap.outlineColor = spec.outlineColor ?? 'rgba(0, 0, 0, 0.6)';
  bitmap.outlineWidth = OUTLINE_WIDTH * scale;
  const lines = spec.text.split('\n');
  // 縁取りの分の余白を残して、すべての行が収まる文字サイズにそろえる
  const usableWidth = Math.max(1, rect.width - OUTLINE_WIDTH * scale);
  const initialSize = scale === 1 ? spec.fontSize : Math.max(1, Math.round(spec.fontSize * scale));
  const fontSize = lines.reduce((size, line) => Math.min(size, fitFontSize(bitmap, line, usableWidth, initialSize)), initialSize);
  bitmap.fontSize = fontSize;
  const lineHeight = Math.round(fontSize * 1.35);
  const top = rect.y + Math.round((rect.height - lineHeight * lines.length) / 2);
  lines.forEach((line, i) => bitmap.drawText(line, rect.x, top + i * lineHeight, rect.width, lineHeight, spec.align ?? 'center'));
};

/** 重ね表示用のスプライトを作る */
const createOverlaySprite = (specs: readonly OverlaySpec[]): Sprite => {
  const container = new Sprite();
  specs.forEach((spec) => {
    const bitmap = new Bitmap(spec.width, spec.height);
    drawOverlaySpec(bitmap, spec, { x: 0, y: 0, width: spec.width, height: spec.height }, 1);
    const sprite = new Sprite(bitmap);
    sprite.x = spec.x;
    sprite.y = spec.y;
    container.addChild(sprite);
  });
  return container;
};

/** 管理用プロパティを読み書きするための Record 表現 */
const meta = (owner: object): Record<string, unknown> => owner as Record<string, unknown>;

/** 付けていた重ね表示を外す */
const detachOverlay = (owner: object): void => {
  const current = meta(owner)[OVERLAY_SPRITE];
  if (current instanceof Sprite) {
    const parent: unknown = meta(current)['parent'];
    if (parent instanceof Sprite) {
      parent.removeChild(current);
    }
  }
  meta(owner)[OVERLAY_SPRITE] = undefined;
  meta(owner)[OVERLAY_KEY] = undefined;
};

/**
 * 画像名と言語に応じて重ね表示を付け替える。
 * 付ける準備ができていない（画像の読み込み中など）場合は、スプライトを作らずに次のフレームで再試行する。
 *
 * @param owner 管理用プロパティを持たせるオブジェクト
 * @param name 現在の画像名
 * @param specs 画像名に対応する定義（無ければ undefined）
 * @param canAttach 今付けられるか（画像の読み込みが終わっているかなど）
 * @param attach 重ね表示を親へ追加する処理
 */
const syncOverlay = (
  owner: object,
  name: string,
  specs: readonly OverlaySpec[] | undefined,
  canAttach: () => boolean,
  attach: (overlay: Sprite) => void,
): void => {
  const key = isJapanese() && specs !== undefined ? name : '';
  if (meta(owner)[OVERLAY_KEY] === key) {
    return;
  }
  detachOverlay(owner);
  if (key === '' || specs === undefined) {
    meta(owner)[OVERLAY_KEY] = key;
    return;
  }
  if (!canAttach()) {
    return;
  }
  // 鍵は作成の前に記録する。作成・追加が例外で失敗しても、同じ画像名・言語のあいだは作り直さない
  // （毎フレーム呼ばれる処理なので、失敗のたびに再試行するとエラー出力が連発する。RA-02）。
  // 画像名か言語が変われば鍵が変わるので、そのときに改めて試みる。
  meta(owner)[OVERLAY_KEY] = key;
  const overlay = createOverlaySprite(specs);
  attach(overlay);
  meta(owner)[OVERLAY_SPRITE] = overlay;
};

/** 画像の読み込みが終わっているか */
const isImageReady = (sprite: Sprite): boolean => {
  const bitmap = sprite.bitmap;
  return bitmap !== null && bitmap.isReady() && bitmap.width > 0;
};

/**
 * 画像を持つスプライトの子として重ね表示を付ける。
 * 子スプライトはスプライトの原点（anchor ではなく位置）が基準になるため、
 * anchor（左上 0 / 中央 0.5 など）に合わせて画像の左上へずらして配置する。
 */
const attachToImageSprite = (sprite: Sprite, overlay: Sprite): void => {
  const bitmap = sprite.bitmap;
  const width = bitmap === null ? 0 : bitmap.width;
  const height = bitmap === null ? 0 : bitmap.height;
  overlay.x = -sprite.anchor.x * width;
  overlay.y = -sprite.anchor.y * height;
  sprite.addChild(overlay);
};

/** ピクチャ（Sprite_Picture）に重ねる */
const updatePictureOverlay = (sprite: Sprite_Picture): void => {
  const name = typeof sprite._pictureName === 'string' ? sprite._pictureName : '';
  syncOverlay(
    sprite,
    name,
    overlaySpecsOf(bundleOverlays().pictures, name),
    () => isImageReady(sprite),
    (overlay) => attachToImageSprite(sprite, overlay),
  );
};

/** タイトル画面の背景（titles1）に重ねる */
const updateTitleOverlay = (scene: Scene_Title): void => {
  const back: unknown = scene._backSprite1;
  if (!(back instanceof Sprite)) {
    return;
  }
  const name = $dataSystem?.title1Name ?? '';
  syncOverlay(
    back,
    name,
    overlaySpecsOf(bundleOverlays().titles1, name),
    () => isImageReady(back),
    (overlay) => attachToImageSprite(back, overlay),
  );
};

/** 遠景の画像の読み込みが終わっているか */
const isParallaxReady = (parallax: TilingSprite): boolean => {
  const bitmap = parallax.bitmap;
  return bitmap !== null && bitmap.isReady() && bitmap.width > 0;
};

/**
 * 遠景（Spriteset_Map の parallax）に重ねる。遠景のスクロールに合わせて毎フレーム位置を更新する。
 * Canvas レンダラでは遠景を差し替えるたびに遠景スプライトが作り直されるため、
 * 管理情報は Spriteset_Map 側に持たせ、遠景スプライトが変わったら付け直す。
 * 遠景は TilingSprite（ツクールMVの Sprite の派生ではない）なので、Sprite かどうかでは判定しない。
 * また PIXI の getChildIndex は子でないと例外を投げるため、位置は children.indexOf で調べる。
 */
const updateParallaxOverlay = (spriteset: Spriteset_Map): void => {
  const parallax: unknown = spriteset._parallax;
  const base: unknown = spriteset._baseSprite;
  if (!(parallax instanceof TilingSprite) || !(base instanceof Sprite)) {
    return;
  }
  // 遠景スプライトが作り直されていたら、古いラベルを外して付け直す
  if (meta(spriteset)[OVERLAY_TARGET] !== parallax) {
    detachOverlay(spriteset);
    meta(spriteset)[OVERLAY_TARGET] = parallax;
  }
  const name = typeof spriteset._parallaxName === 'string' ? spriteset._parallaxName : '';
  syncOverlay(
    spriteset,
    name,
    overlaySpecsOf(bundleOverlays().parallaxes, name),
    () => isParallaxReady(parallax) && base.children.indexOf(parallax) >= 0,
    (overlay) => {
      // 遠景の直上（タイルマップより下）に入れる
      base.addChildAt(overlay, base.children.indexOf(parallax) + 1);
    },
  );
  const overlay = meta(spriteset)[OVERLAY_SPRITE];
  if (overlay instanceof Sprite) {
    overlay.x = -parallax.origin.x;
    overlay.y = -parallax.origin.y;
  }
};

/**
 * 毎フレームの更新処理の後に重ね表示を同期するラッパー。
 * this が期待する型でない場合（他プラグインが別の文脈で呼んだ場合など）は何もしない。
 * 同期の失敗は報告するが、同じ対象で同じ内容の失敗が続く場合は最初の 1 回だけ報告する
 * （毎フレーム呼ばれるため、そのままではコンソールがエラーで埋まる。RA-02）。
 */
const afterUpdate =
  <T extends object>(isTarget: (value: unknown) => value is T, sync: (target: T) => void, label: string) =>
  (original: Method): Method =>
    function (this: unknown, ...args: unknown[]): unknown {
      const result = original.apply(this, args);
      if (isTarget(this)) {
        try {
          sync(this);
        } catch (error) {
          const message = error instanceof Error ? error.message : String(error);
          if (meta(this)[OVERLAY_ERROR] !== message) {
            meta(this)[OVERLAY_ERROR] = message;
            reportError(label, error);
          }
        }
      }
      return result;
    };

/** 描画先のフォント設定（描いた後に元へ戻すため） */
interface FontSettings {
  readonly fontFace: string;
  readonly fontSize: number;
  readonly fontItalic: boolean;
  readonly textColor: string;
  readonly outlineColor: string;
  readonly outlineWidth: number;
}

/** ビットマップのフォント設定を取り出す */
const saveFontSettings = (bitmap: Bitmap): FontSettings => ({
  fontFace: bitmap.fontFace,
  fontSize: bitmap.fontSize,
  fontItalic: bitmap.fontItalic,
  textColor: bitmap.textColor,
  outlineColor: bitmap.outlineColor,
  outlineWidth: bitmap.outlineWidth,
});

/** ビットマップのフォント設定を戻す */
const restoreFontSettings = (bitmap: Bitmap, saved: FontSettings): void => {
  bitmap.fontFace = saved.fontFace;
  bitmap.fontSize = saved.fontSize;
  bitmap.fontItalic = saved.fontItalic;
  bitmap.textColor = saved.textColor;
  bitmap.outlineColor = saved.outlineColor;
  bitmap.outlineWidth = saved.outlineWidth;
};

/**
 * 画像の座標の矩形を、縮小して描かれた画像の上の整数座標へ変換する（純粋関数）。
 * 縮小・切り捨てによる 1px 未満のずれで英字の端が覗かないよう、外側へ丸める。
 */
const scaleRect = (spec: OverlaySpec, scaleX: number, scaleY: number): Rect => {
  const left = Math.floor(spec.x * scaleX);
  const top = Math.floor(spec.y * scaleY);
  const right = Math.ceil((spec.x + spec.width) * scaleX);
  const bottom = Math.ceil((spec.y + spec.height) * scaleY);
  return { x: left, y: top, width: right - left, height: bottom - top };
};

/**
 * YEP_CommonEventMenu の「ピクチャ窓」（Window_CommonEventMenuPicture）に日本語ラベルを描く（RA-01）。
 *
 * エリアマップ（BaseMap / BaseMap2）は「ピクチャの表示」ではなく、この窓の drawPicture が画像を
 * contents へ縮小して blt して表示する。Sprite_Picture を通らないため、元の drawPicture が描いた直後に、
 * YEP と同じ縮小率・切り捨てで縮めた背景と日本語を同じ contents に描く。
 * YEP は描き直すたびに contents.clear() してから画像を描くので、ラベルが重複して残ることは無い。
 *
 * 次の場合は何もしない。
 * - 英語表示のとき
 * - 画像の読み込み前（幅 0）。元の drawPicture は描かずに setTimeout で自分を呼び直すだけで、
 *   読み込み後の呼び直しで実際に描いたときに、改めてここへ来る
 * - 定義の無い画像（同じ窓で表示する人物 DB の肖像など）
 *
 * @param win drawPicture を呼んだ窓（他プラグインから別の文脈で呼ばれる可能性もあるので unknown で受ける）
 */
const drawCommonEventMenuPictureOverlay = (win: unknown): void => {
  if (!isJapanese() || !isRecord(win)) {
    return;
  }
  const name = win['_picture'];
  const source = win['_pictureBitmap'];
  const contents = win['contents'];
  if (typeof name !== 'string' || name === '' || !(source instanceof Bitmap) || !(contents instanceof Bitmap)) {
    return;
  }
  const pw = source.width;
  const ph = source.height;
  if (pw <= 0 || ph <= 0) {
    return;
  }
  const specs = overlaySpecsOf(bundleOverlays().pictures, name);
  if (specs === undefined) {
    return;
  }
  // YEP_CommonEventMenu の drawPicture と同じ計算: 窓より大きい画像だけを縦横比を保って縮め、描く大きさは切り捨てる
  const rate = Math.min(pw > contents.width ? contents.width / pw : 1, ph > contents.height ? contents.height / ph : 1);
  const dw = Math.floor(pw * rate);
  const dh = Math.floor(ph * rate);
  if (dw <= 0 || dh <= 0) {
    return;
  }
  // 実際に描かれた画像の縦横それぞれの縮尺（切り捨ての分まで画像と一致させる）
  const scaleX = dw / pw;
  const scaleY = dh / ph;
  // 窓のフォント設定を変えたままにしない（他の描画に影響させないため、描いた後に戻す）
  const saved = saveFontSettings(contents);
  try {
    specs.forEach((spec) => drawOverlaySpec(contents, spec, scaleRect(spec, scaleX, scaleY), rate));
  } finally {
    restoreFontSettings(contents, saved);
  }
};

/** ピクチャ窓の drawPicture の後に日本語ラベルを描くラッパー（元の処理の戻り値・例外はそのまま） */
const afterDrawPicture =
  (original: Method): Method =>
  function (this: unknown, ...args: unknown[]): unknown {
    const result = original.apply(this, args);
    try {
      drawCommonEventMenuPictureOverlay(this);
    } catch (error) {
      reportError('メニューの画像の日本語ラベル表示', error);
    }
    return result;
  };

/** フックを差し込む */
export const installOverlayHooks = (): void => {
  wrapMethod(
    Sprite_Picture.prototype as unknown as Record<string, unknown>,
    'update',
    afterUpdate((v: unknown): v is Sprite_Picture => v instanceof Sprite_Picture, updatePictureOverlay, 'ピクチャの日本語ラベル表示'),
    'Sprite_Picture.prototype',
  );
  wrapMethod(
    Spriteset_Map.prototype as unknown as Record<string, unknown>,
    'updateParallax',
    afterUpdate((v: unknown): v is Spriteset_Map => v instanceof Spriteset_Map, updateParallaxOverlay, '遠景の日本語ラベル表示'),
    'Spriteset_Map.prototype',
  );
  wrapMethod(
    Scene_Title.prototype as unknown as Record<string, unknown>,
    'update',
    afterUpdate((v: unknown): v is Scene_Title => v instanceof Scene_Title, updateTitleOverlay, 'タイトル画面の日本語ラベル表示'),
    'Scene_Title.prototype',
  );
  // エリアマップ（YEP_CommonEventMenu のピクチャ窓）。プラグインが無ければ警告を出して省略する
  wrapMethod(getPrototype('Window_CommonEventMenuPicture'), 'drawPicture', afterDrawPicture, 'Window_CommonEventMenuPicture.prototype');
};
