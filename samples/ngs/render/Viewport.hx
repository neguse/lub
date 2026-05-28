package render;

class Viewport {
  public static inline var X: Int = 200;
  public static inline var Y: Int = 0;
  public static inline var W: Int = 240;
  public static inline var H: Int = 480;
  public static inline function sx(worldX: Int): Int return worldX - X;
  public static inline function sy(worldY: Int): Int return worldY - Y;
}
