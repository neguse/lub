#include "../../src/haxe_build.h"
#include "../../src/haxe_server.h"
#include <SDL3/SDL.h>
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

int main(void) {
  // テスト用 sandbox を作る。
  SDL_CreateDirectory("/tmp/build_smoke");
  // 既存の生成物 (前回失敗の残骸) があれば掃除
  SDL_RemovePath("/tmp/build_smoke/.lub/Main.lua");
  SDL_RemovePath("/tmp/build_smoke/.lub/Main.raw.tmp");
  SDL_RemovePath("/tmp/build_smoke/.lub/Main.lua.tmp");

  FILE *fx = fopen("/tmp/build_smoke/Main.hx", "w");
  assert(fx);
  fprintf(
      fx,
      "import lub.Phys2d;\n"
      "class Main {\n"
      "  public static function main() {}\n"
      "  public static function onFrame() {\n"
      "    var world = Phys2d.world(\"smoke\", {\n"
      "      gravity: { x: 0.0, y: -9.8 }, fixedDt: 1.0 / 60.0,\n"
      "      substeps: 4, maxSteps: 1,\n"
      "      callbacks: {\n"
      "        filter: function(a:Dynamic, b:Dynamic):Bool return true,\n"
      "        preSolve: function(c:Dynamic):Bool return true,\n"
      "        friction: function(a:Dynamic, b:Dynamic):Float return 0.6,\n"
      "        restitution: function(a:Dynamic, b:Dynamic):Float return 0.0\n"
      "      }\n"
      "    });\n"
      "    Phys2d.begin(world);\n"
      "    var wi = Phys2d.worldInfo(world);\n"
      "    var body = Phys2d.body(world, \"body\", {\n"
      "      type: Phys2d.DYNAMIC, enabled: true, awake: true,\n"
      "      sleep: true, sleepThreshold: 0.05,\n"
      "      initial: { x: 0.0, y: 1.0 }\n"
      "    });\n"
      "    var other = Phys2d.body(world, \"other\", {\n"
      "      type: Phys2d.DYNAMIC, initial: { x: 1.0, y: 1.0 }\n"
      "    });\n"
      "    var shape = Phys2d.box(body, \"shape\", {\n"
      "      hx: 0.5, hy: 0.5, density: 1.0, contact: true\n"
      "    });\n"
      "    var circle = Phys2d.circle(body, \"circle\", {\n"
      "      r: 0.2, density: 1.0, sensorEvents: true\n"
      "    });\n"
      "    var capsule = Phys2d.capsule(body, \"capsule\", {\n"
      "      ax: -0.2, ay: 0.0, bx: 0.2, by: 0.0, r: 0.1, density: 1.0\n"
      "    });\n"
      "    var segment = Phys2d.segment(body, \"segment\", {\n"
      "      ax: -1.0, ay: -0.5, bx: 1.0, by: -0.5, contact: true\n"
      "    });\n"
      "    var polygon = Phys2d.polygon(body, \"polygon\", {\n"
      "      points: [{ x: -0.2, y: -0.1 }, { x: 0.2, y: -0.1 },\n"
      "        { x: 0.15, y: 0.2 }, { x: -0.15, y: 0.2 }],\n"
      "      radius: 0.01, density: 1.0\n"
      "    });\n"
      "    var chain = Phys2d.chain(body, \"chain\", {\n"
      "      version: 1,\n"
      "      points: [{ x: -2.0, y: -1.0 }, { x: -0.5, y: -1.0 },\n"
      "        { x: 0.5, y: -1.0 }, { x: 2.0, y: -1.0 }],\n"
      "      materials: [{ material: 3, friction: 0.7 }],\n"
      "      sensorEvents: true\n"
      "    });\n"
      "    var chainSegments = Phys2d.chainSegments(chain);\n"
      "    Phys2d.shapeSetMaterial(shape, { material: \"smoke\", "
      "userMaterialId: 2 });\n"
      "    Phys2d.shapeSetFilter(shape, { category: 1, mask: \"all\" });\n"
      "    Phys2d.shapeSetEvents(shape, { contact: true, hit: false });\n"
      "    var si = Phys2d.shapeInfo(shape);\n"
      "    var joint = Phys2d.joint(world, \"joint\", {\n"
      "      type: \"distance\", bodyA: body, bodyB: other,\n"
      "      localAnchorA: { x: 0.0, y: 0.0 }, localAnchorB: { x: 0.0, y: 0.0 "
      "},\n"
      "      length: 1.0, hertz: 2.0, dampingRatio: 0.5\n"
      "    });\n"
      "    var jointInfo = Phys2d.jointInfo(joint);\n"
      "    var jointForce = Phys2d.jointForce(joint);\n"
      "    var jointTorque = Phys2d.jointTorque(joint);\n"
      "    var jointAngle = Phys2d.jointAngle(joint);\n"
      "    var jointTranslation = Phys2d.jointTranslation(joint);\n"
      "    var jointSpeed = Phys2d.jointSpeed(joint);\n"
      "    var jointLength = Phys2d.jointLength(joint);\n"
      "    var jointMotorForce = Phys2d.jointMotorForce(joint);\n"
      "    var jointMotorTorque = Phys2d.jointMotorTorque(joint);\n"
      "    Phys2d.jointSetMotor(joint, { enabled: true, speed: 1.0, maxForce: "
      "5.0, maxTorque: 5.0 });\n"
      "    Phys2d.jointSetLimit(joint, { enabled: true, lower: 0.1, upper: 2.0 "
      "});\n"
      "    Phys2d.jointSetSpring(joint, { enabled: true, hertz: 3.0, "
      "dampingRatio: 0.6 });\n"
      "    Phys2d.jointSetTarget(joint, { target: { x: 0.0, y: 0.0 }, "
      "linearOffset: { x: 0.0, y: 0.0 }, angularOffset: 0.0 });\n"
      "    Phys2d.addForce(body, { x: 0.0, y: 1.0 }, { point: { x: 0.0, y: 1.0 "
      "} });\n"
      "    Phys2d.addForceCenter(body, { x: 0.0, y: 1.0 });\n"
      "    Phys2d.addImpulse(body, { x: 0.0, y: 0.1 }, { px: 0.0, py: 1.0 });\n"
      "    Phys2d.addImpulseCenter(body, { x: 0.0, y: 0.1 });\n"
      "    Phys2d.addTorque(body, 0.1);\n"
      "    Phys2d.addAngularImpulse(body, 0.1);\n"
      "    Phys2d.setVelocity(body, { vx: 0.0, vy: 0.0, w: 0.0 }, { wake: "
      "false });\n"
      "    Phys2d.teleport(body, { x: 0.0, y: 1.0, angle: 0.0 }, { wake: false "
      "});\n"
      "    Phys2d.setTarget(body, { x: 0.0, y: 1.0, angle: 0.0 }, { timeStep: "
      "1.0 / 60.0 });\n"
      "    Phys2d.setMassData(body, { mass: 1.0, inertia: 1.0, center: { x: "
      "0.0, y: 0.0 } });\n"
      "    Phys2d.step(world, 1.0 / 60.0);\n"
      "    var pose = Phys2d.pose(body);\n"
      "    var poseEnabled:Bool = pose.enabled;\n"
      "    var poseSleep:Bool = pose.sleep;\n"
      "    var poseSleepThreshold:Float = pose.sleep_threshold;\n"
      "    var poseByKey = Phys2d.pose(world, \"body\");\n"
      "    var velocity = Phys2d.velocity(body);\n"
      "    var mass = Phys2d.mass(body);\n"
      "    var center = Phys2d.center(body);\n"
      "    var worldPoint = Phys2d.worldPoint(body, { x: 0.0, y: 0.0 });\n"
      "    var localPoint = Phys2d.localPoint(body, { x: 0.0, y: 0.0 });\n"
      "    var velocityAt = Phys2d.velocityAt(body, { x: 0.0, y: 0.0 });\n"
      "    var bodyShapes = Phys2d.bodyShapes(body);\n"
      "    var bodyJoints = Phys2d.bodyJoints(body);\n"
      "    var bodyContacts = Phys2d.bodyContacts(body);\n"
      "    var tested = Phys2d.shapeTestPoint(shape, { x: 0.0, y: 1.0 });\n"
      "    var shapeRay = Phys2d.shapeRaycast(shape, { x: 0.0, y: 2.0, dx: "
      "0.0, dy: -1.0 });\n"
      "    var closest = Phys2d.shapeClosestPoint(shape, { x: 0.0, y: 2.0 });\n"
      "    var aabb = Phys2d.shapeAabb(shape);\n"
      "    var contacts = Phys2d.contacts(world, \"begin\");\n"
      "    var bodyEvents = Phys2d.bodyEvents(world);\n"
      "    var sensors = Phys2d.sensors(world, \"begin\");\n"
      "    var hit = Phys2d.raycast(world, {\n"
      "      x: 0.0, y: 2.0, dx: 0.0, dy: -4.0,\n"
      "      filter: { mask: \"all\" }\n"
      "    }, function(h:Dynamic):Dynamic { return \"clip\"; });\n"
      "    var overlaps = Phys2d.overlapAabb(world, {\n"
      "      minX: -2.0, minY: -2.0, maxX: 2.0, maxY: 2.0,\n"
      "      filter: { mask: \"all\" }\n"
      "    }, function(h:Dynamic):Dynamic { return true; });\n"
      "    var shapeCastHit = Phys2d.shapeCast(world, {\n"
      "      type: \"circle\", r: 0.1, x: 0.0, y: 2.0, dx: 0.0, dy: -1.0,\n"
      "      filter: { mask: \"all\" }\n"
      "    }, function(h:Dynamic):Dynamic { return \"clip\"; });\n"
      "    var moverHit = Phys2d.castMover(world, {\n"
      "      ax: -0.1, ay: 0.0, bx: 0.1, by: 0.0, r: 0.2,\n"
      "      dx: 0.0, dy: -1.0, filter: { mask: \"all\" }\n"
      "    });\n"
      "    var moverPlanes = Phys2d.collideMover(world, {\n"
      "      ax: -0.1, ay: 0.0, bx: 0.1, by: 0.0, r: 0.2,\n"
      "      filter: { mask: \"all\" }\n"
      "    }, function(h:Dynamic):Dynamic { return true; });\n"
      "    Phys2d.explode(world, { x: 0.0, y: 0.0, radius: 1.0, impulse: 0.1 "
      "});\n"
      "    var debug = Phys2d.debug(world, { shapes: true });\n"
      "    var profile = Phys2d.profile(world);\n"
      "    var counters = Phys2d.counters(world);\n"
      "    if (wi == null || si == null || pose == null || hit == null || "
      "chainSegments == null\n"
      "      || circle == null || capsule == null || segment == null || "
      "polygon == null\n"
      "      || jointInfo == null || jointForce == null || jointTorque == null "
      "|| jointAngle == null\n"
      "      || jointTranslation == null || jointSpeed == null || jointLength "
      "== null\n"
      "      || jointMotorForce == null || jointMotorTorque == null || "
      "poseByKey == null\n"
      "      || velocity == null || mass == null || center == null || "
      "worldPoint == null\n"
      "      || localPoint == null || velocityAt == null || bodyShapes == null "
      "|| bodyJoints == null\n"
      "      || bodyContacts == null || tested == false || shapeRay == null || "
      "closest == null\n"
      "      || aabb == null || contacts == null || bodyEvents == null || "
      "sensors == null\n"
      "      || overlaps == null || shapeCastHit == null || moverHit == null "
      "|| "
      "moverPlanes == null\n"
      "      || debug == null || profile == null || counters == null) {}\n"
      "  }\n"
      "}\n");
  fclose(fx);

  FILE *fh = fopen("/tmp/build_smoke/Main.hxml", "w");
  assert(fh);
  fprintf(fh, "-cp /tmp/build_smoke\n-lib lub\n-main Main\n");
  fclose(fh);

  HaxeServer s;
  if (!haxe_server_start(&s)) {
    SDL_Log("server start failed");
    return 1;
  }
  HxmlMeta m;
  if (!hxml_parse("/tmp/build_smoke/Main.hxml", &m)) {
    SDL_Log("hxml_parse failed");
    haxe_server_stop(&s);
    return 1;
  }
  HaxeBuildResult r = haxe_build_run(&s, "/tmp/build_smoke/Main.hxml", &m);
  haxe_server_stop(&s);
  if (!r.ok) {
    SDL_Log("build failed: %s", r.log);
    return 1;
  }

  // 生成された .lub/Main.lua を確認
  FILE *fc = fopen("/tmp/build_smoke/.lub/Main.lua", "rb");
  if (!fc) {
    SDL_Log("expected output Main.lua not found");
    return 1;
  }
  char buf[65536];
  size_t n = fread(buf, 1, sizeof(buf) - 1, fc);
  buf[n] = '\0';
  fclose(fc);

  // prelude marker (HAXE_PRELUDE は Haxe 互換層のみ。namespace table は
  // samples/lub_prelude.lua が boot 時に注入するので出力には含まれない)
  if (!strstr(buf, "lua-utf8")) {
    SDL_Log("prelude marker 'lua-utf8' not found in output");
    return 1;
  }
  if (!strstr(buf, "bit32")) {
    SDL_Log("prelude marker 'bit32' not found in output");
    return 1;
  }
  // postlude marker
  if (!strstr(buf, "return Main")) {
    SDL_Log("postlude 'return Main' not found in output");
    return 1;
  }
  // API 面のカバレッジは tests/lua/test_api_surface.lua (cs-lib/lub_stub.cs
  // からの生成物) が検査する。

  // raw.tmp が掃除されていることを確認
  FILE *raw = fopen("/tmp/build_smoke/.lub/Main.raw.tmp", "rb");
  if (raw) {
    fclose(raw);
    SDL_Log("raw.tmp was not cleaned up");
    return 1;
  }

  printf("OK\n");
  return 0;
}
