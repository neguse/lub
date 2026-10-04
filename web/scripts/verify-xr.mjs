import assert from "node:assert/strict";
import { frameScheduler, readInputs } from "../xr.mjs";

const pad = {
  mapping: "xr-standard",
  axes: [0, 0, -0.8, -0.9],
  buttons: Array.from({ length: 7 }, () => ({ value: 0, pressed: false })),
};
pad.buttons[0] = { value: 0.7, pressed: true };
pad.buttons[4] = { value: 1, pressed: true };
const live = { handedness: "right", profiles: ["oculus-touch"], gamepad: pad };
const placeholder = {
  ...live,
  profiles: [],
  gamepad: { ...pad, axes: [0, 0, 0, 0] },
};
for (const sources of [
  [live, placeholder],
  [placeholder, live],
]) {
  const [left, right] = readInputs(sources);
  assert.equal(left, null);
  assert.equal(right.stickX, -0.8);
  assert.equal(right.stickY, 0.9);
  assert.equal(right.trigger, 0.7);
  assert.equal(right.primary, true);
  assert.equal(right.secondary, false);
}
assert.deepEqual(readInputs([]), [null, null]);
assert.deepEqual(readInputs([{ ...live, gamepad: { ...pad, mapping: "" } }]), [
  null,
  null,
]);
const callbacks = new Map();
let id = 0;
const host = {
  requestAnimationFrame(callback) {
    callbacks.set(++id, callback);
    return id;
  },
  cancelAnimationFrame(id) {
    callbacks.delete(id);
  },
};
const scheduler = frameScheduler(host),
  calls = [];
host.requestAnimationFrame(() => {
  calls.push(1);
  host.requestAnimationFrame(() => calls.push(2));
});
scheduler.immersive(true);
assert.equal(callbacks.size, 0);
scheduler.flush(10);
assert.deepEqual(calls, [1]);
scheduler.flush(20);
assert.deepEqual(calls, [1, 2]);
const cancelled = host.requestAnimationFrame(() => calls.push(3));
host.cancelAnimationFrame(cancelled);
scheduler.flush(30);
assert.deepEqual(calls, [1, 2]);
host.requestAnimationFrame(() => calls.push(4));
scheduler.immersive(false);
assert.equal(callbacks.size, 1);
console.log(
  "WebXR raw controls, duplicate input sources and frame scheduling passed",
);
