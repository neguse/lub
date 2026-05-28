package input;

interface InputSource {
  public function refresh(): Void;       // 毎フレーム冒頭で 1 回
  public var current(get, never): InputSnapshot;
}
