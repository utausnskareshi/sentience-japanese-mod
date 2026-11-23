/**
 * RPGツクールMV 1.3.4 のコアスクリプトが定義するグローバルの型宣言（本プラグインで使う部分のみ）。
 *
 * コアスクリプト自体は改変・同梱せず、実行時に存在するものを参照するだけ。
 * 他プラグインが追加するクラス（GALV / YEP / Gameus）は存在しない可能性があるため、
 * ここでは宣言せず env.ts の補助関数で unknown として安全に取り出す。
 */

declare class Bitmap {
  constructor(width?: number, height?: number);
  readonly width: number;
  readonly height: number;
  fontFace: string;
  fontSize: number;
  fontItalic: boolean;
  textColor: string;
  outlineColor: string;
  outlineWidth: number;
  isReady(): boolean;
  fillRect(x: number, y: number, width: number, height: number, color: string): void;
  drawText(text: string, x: number, y: number, maxWidth: number, lineHeight: number, align?: string): void;
  measureTextWidth(text: string): number;
}

/** PIXI.Sprite を拡張したツクールMVのスプライト（使う部分のみ） */
declare class Sprite {
  constructor(bitmap?: Bitmap);
  bitmap: Bitmap | null;
  x: number;
  y: number;
  visible: boolean;
  readonly anchor: { x: number; y: number };
  readonly children: unknown[];
  addChild<T>(child: T): T;
  addChildAt<T>(child: T, index: number): T;
  removeChild<T>(child: T): T;
  getChildIndex(child: unknown): number;
}

/**
 * 遠景などに使うタイル状のスプライト（使う部分のみ）。
 * PIXI の TilingSprite を拡張したもので、ツクールMVの Sprite の派生ではない（instanceof Sprite は false）。
 */
declare class TilingSprite {
  bitmap: Bitmap | null;
  readonly origin: { x: number; y: number };
}

declare class Sprite_Picture extends Sprite {
  _pictureName: string;
}

declare class Spriteset_Map {
  _parallax: TilingSprite;
  _parallaxName: string;
  _baseSprite: Sprite;
  updateParallax(): void;
}

declare class Scene_Title {
  _backSprite1: Sprite;
  update(): void;
}

declare const $dataSystem: { readonly title1Name: string } | null;

declare class Window_Base {
  static _faceWidth: number;
  contents: Bitmap;
  contentsWidth(): number;
  standardPadding(): number;
  textPadding(): number;
  standardFontFace(): string;
  standardFontSize(): number;
  resetFontSettings(): void;
  convertEscapeCharacters(text: string): string;
}

declare class Window_Help extends Window_Base {
  setText(text: string): void;
}

declare class Window_Command extends Window_Base {
  addCommand(name: string, symbol: string, enabled?: boolean, ext?: unknown): void;
  commandSymbol(index: number): string;
  commandName(index: number): string;
}

declare class Window_Options extends Window_Command {
  addGeneralOptions(): void;
  statusText(index: number): string;
  refresh(): void;
}

declare class Game_Message {
  _texts: string[];
  _choices: string[];
  isBusy(): boolean;
  faceName(): string;
  setChoices(choices: string[], defaultType: number, cancelType: number): void;
}

declare class Game_Interpreter {
  command101(): boolean;
  command105(): boolean;
}

declare class Game_Actor {
  name(): string;
  nickname(): string;
  profile(): string;
}

declare class Game_Actors {
  actor(actorId: number): Game_Actor | null;
}

declare class Game_Party {
  members(): Game_Actor[];
}

declare class Game_Variables {
  value(variableId: number): unknown;
}

declare class Scene_Boot {
  isReady(): boolean;
}

declare const Graphics: {
  readonly boxWidth: number;
  readonly boxHeight: number;
};

declare const DataManager: {
  onLoad(object: unknown): void;
  isDatabaseLoaded(): boolean;
};

declare const ConfigManager: {
  makeData(): Record<string, unknown>;
  applyData(config: Record<string, unknown>): void;
};

declare const TextManager: {
  readonly currencyUnit: string;
};

declare const $gameMessage: Game_Message;
declare const $gameActors: Game_Actors | null;
declare const $gameParty: Game_Party | null;
declare const $gameVariables: Game_Variables | null;
