package input;

class InputSnapshot {
  public var dirX: Int = 0;   // -1/0/1
  public var dirY: Int = 0;   // -1/0/1 (down = +1, 画面下方向)
  public var fire: Bool = false;    // Z held
  public var slow: Bool = false;    // X held
  public var menu: Bool = false;    // Z pressed-this-frame (trigger)
  public var cancel: Bool = false;  // ESC trigger
  public var noGod: Bool = false;   // C held
  public function new() {}
}
