package lub;

@:multiReturn extern class MouseDelta {
  var dx: Float;
  var dy: Float;
}

extern class Input {
  @:native("key_down")     public static function keyDown(code: String): Bool;
  // relative mouse motion (window px) since the last call; call once per frame.
  @:native("mouse_delta")  public static function mouseDelta(): MouseDelta;
  // button: 1=left (default), 2=middle, 3=right.
  @:native("mouse_down")   public static function mouseDown(?button: Int): Bool;
}
