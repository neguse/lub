export function readInputs(sources) {
  const inputs = [null, null],
    ranks = [-1, -1];
  for (const source of sources) {
    const hand =
      source.handedness === "left" ? 0 : source.handedness === "right" ? 1 : -1;
    const pad = source.gamepad;
    if (hand < 0 || !pad || pad.mapping !== "xr-standard") continue;
    const rank = source.profiles?.length || 0;
    if (rank < ranks[hand]) continue;
    ranks[hand] = rank;
    const value = (index) => pad.buttons[index]?.value || 0;
    const pressed = (index) => !!pad.buttons[index]?.pressed;
    inputs[hand] = {
      active: true,
      stickX: pad.axes[2] || 0,
      stickY: -(pad.axes[3] || 0),
      trigger: value(0),
      grip: value(1),
      primary: pressed(4),
      secondary: pressed(5),
      menu: pressed(6),
      stickClick: pressed(3),
    };
  }
  return inputs;
}

export function frameScheduler(host) {
  const request = host.requestAnimationFrame.bind(host),
    cancel = host.cancelAnimationFrame.bind(host);
  const pending = new Map();
  let sequence = 0,
    native = null,
    immersive = false;
  function schedule() {
    if (!immersive && native === null && pending.size)
      native = request((time) => {
        native = null;
        flush(time);
        schedule();
      });
  }
  function flush(time) {
    const callbacks = [...pending.entries()];
    for (const [id, callback] of callbacks) {
      if (!pending.delete(id)) continue;
      callback(time);
    }
  }
  // SDL's Emscripten loop must run inside the XR frame, before copying the stereo canvas.
  host.requestAnimationFrame = (callback) => {
    const id = ++sequence;
    pending.set(id, callback);
    schedule();
    return id;
  };
  host.cancelAnimationFrame = (id) => pending.delete(id);
  return {
    flush,
    immersive(value) {
      immersive = value;
      if (native !== null) {
        cancel(native);
        native = null;
      }
      schedule();
    },
  };
}

function presenter(gl) {
  const shaders = [];
  const program = gl.createProgram(),
    texture = gl.createTexture(),
    vao = gl.createVertexArray();
  function dispose() {
    shaders.forEach((shader) => gl.deleteShader(shader));
    gl.deleteProgram(program);
    gl.deleteTexture(texture);
    gl.deleteVertexArray(vao);
  }
  try {
    for (const [type, source] of [
      [
        gl.VERTEX_SHADER,
        `#version 300 es
                out vec2 uv;
                void main() {
                    vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
                    uv = p; gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
                }`,
      ],
      [
        gl.FRAGMENT_SHADER,
        `#version 300 es
                precision highp float;
                uniform sampler2D scene;
                uniform float eye;
                in vec2 uv;
                out vec4 color;
                void main() { color = texture(scene, vec2((uv.x + eye) * 0.5, uv.y)); }`,
      ],
    ]) {
      const shader = gl.createShader(type);
      shaders.push(shader);
      gl.shaderSource(shader, source);
      gl.compileShader(shader);
      if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS))
        throw new Error(gl.getShaderInfoLog(shader));
      gl.attachShader(program, shader);
    }
    gl.linkProgram(program);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS))
      throw new Error(gl.getProgramInfoLog(program));
    const eye = gl.getUniformLocation(program, "eye");
    gl.bindTexture(gl.TEXTURE_2D, texture);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, true);
    return {
      dispose,
      draw(canvas, layer, views, rendered) {
        gl.bindFramebuffer(gl.FRAMEBUFFER, layer.framebuffer);
        gl.clearColor(0, 0, 0, 1);
        gl.clear(gl.COLOR_BUFFER_BIT);
        if (!rendered) return;
        gl.useProgram(program);
        gl.bindVertexArray(vao);
        gl.bindTexture(gl.TEXTURE_2D, texture);
        gl.texImage2D(
          gl.TEXTURE_2D,
          0,
          gl.RGBA,
          gl.RGBA,
          gl.UNSIGNED_BYTE,
          canvas,
        );
        for (const view of views) {
          const viewport = layer.getViewport(view);
          gl.viewport(viewport.x, viewport.y, viewport.width, viewport.height);
          gl.uniform1f(eye, view.eye === "left" ? 0 : 1);
          gl.drawArrays(gl.TRIANGLES, 0, 3);
        }
      },
    };
  } catch (error) {
    dispose();
    throw error;
  }
}

export async function createXR({
  canvas,
  button,
  getModule,
  report,
  maxDimension = Infinity,
  unlockAudio = () => {},
}) {
  let ready = false,
    session = null,
    pending = false,
    output,
    scheduler,
    savedSize;
  const api = {
    ready() {
      ready = true;
      if (scheduler) button.disabled = false;
    },
  };
  button.hidden = false;
  try {
    if (
      !globalThis.isSecureContext ||
      !navigator.xr ||
      !globalThis.XRWebGLLayer ||
      !(await navigator.xr.isSessionSupported("immersive-vr"))
    ) {
      button.textContent = "VR非対応";
      return api;
    }
  } catch {
    button.textContent = "VR非対応";
    return api;
  }
  scheduler = frameScheduler(window);
  button.textContent = "VRで遊ぶ";
  function cleanup(ended) {
    if (session !== ended) return;
    session = null;
    output?.dispose();
    output = null;
    if (savedSize) {
      [canvas.width, canvas.height, window._canvasWidth, window._canvasHeight] =
        savedSize;
      savedSize = null;
    }
    getModule().lubXR = null;
    scheduler.immersive(false);
    button.textContent = "VRで遊ぶ";
    button.disabled = !ready;
    canvas.focus();
  }
  button.onclick = async () => {
    if (pending) return;
    pending = true;
    button.disabled = true;
    try {
      if (session) {
        await session.end();
        return;
      }
      unlockAudio();
      const current = await navigator.xr.requestSession("immersive-vr", {
        requiredFeatures: ["local"],
      });
      session = current;
      getModule().lubXR = {
        views: null,
        inputs: [null, null],
        focused: false,
        rendered: false,
      };
      current.addEventListener("end", () => cleanup(current), { once: true });
      const surface = document.createElement("canvas");
      const gl = surface.getContext("webgl2", {
        xrCompatible: true,
        alpha: false,
        antialias: false,
      });
      if (!gl) throw new Error("VR用の描画を初期化できませんでした。");
      surface.addEventListener("webglcontextlost", (event) => {
        event.preventDefault();
        report(new Error("VRの描画が停止しました。"));
        current.end().catch(report);
      });
      await gl.makeXRCompatible();
      if (session !== current) return;
      const layer = new XRWebGLLayer(current, gl, {
        alpha: false,
        antialias: false,
        depth: false,
        stencil: false,
      });
      current.updateRenderState({
        baseLayer: layer,
        depthNear: 0.05,
        depthFar: 500,
      });
      const reference = await current.requestReferenceSpace("local");
      if (session !== current) return;
      output = presenter(gl);
      savedSize = [
        canvas.width,
        canvas.height,
        window._canvasWidth,
        window._canvasHeight,
      ];
      scheduler.immersive(true);
      button.textContent = "VRを終了";
      button.disabled = false;
      function frame(time, xrFrame) {
        if (session !== current) return;
        try {
          const pose = xrFrame.getViewerPose(reference);
          const views =
            pose &&
            ["left", "right"].map((eye) =>
              pose.views.find((view) => view.eye === eye),
            );
          const valid = views?.every(Boolean);
          const focused = current.visibilityState === "visible" && valid;
          const viewport = valid ? layer.getViewport(views[0]) : null;
          const scale = viewport
            ? Math.min(
                1,
                maxDimension / Math.max(viewport.width, viewport.height),
              )
            : 1;
          const width = viewport
            ? Math.max(1, Math.round(viewport.width * scale))
            : 0;
          const height = viewport
            ? Math.max(1, Math.round(viewport.height * scale))
            : 0;
          if (
            valid &&
            (canvas.width !== width * 2 || canvas.height !== height)
          ) {
            canvas.width = window._canvasWidth = width * 2;
            canvas.height = window._canvasHeight = height;
          }
          const state = {
            views: valid ? views : null,
            width,
            height,
            focused,
            inputs: focused ? readInputs(current.inputSources) : [null, null],
            rendered: false,
          };
          getModule().lubXR = state;
          scheduler.flush(time);
          output.draw(
            canvas,
            layer,
            focused ? views : [],
            state.rendered && focused,
          );
          current.requestAnimationFrame(frame);
        } catch (error) {
          report(error);
          current.end().catch(report);
        }
      }
      current.requestAnimationFrame(frame);
    } catch (error) {
      const current = session;
      if (current) {
        try {
          await current.end();
        } catch (endError) {
          report(endError);
        }
        cleanup(current);
      }
      report(error);
    } finally {
      pending = false;
      button.disabled = !ready;
    }
  };
  return api;
}
