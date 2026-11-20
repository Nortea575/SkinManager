const createSkinFillEngine = (host) => {
  const readInlineFill = (element) => ({
    value: element.style.getPropertyValue("background-color"),
    priority: element.style.getPropertyPriority("background-color"),
  });

  const sameInlineFill = (left, right) => !!left && !!right
    && left.value === right.value && left.priority === right.priority;

  const currentState = (owner, definedStates) => {
    for (const state of host.statePriority) {
      if (definedStates?.[state] !== undefined && matchesState(owner, state)) return state;
    }
    return null;
  };

  const matchesState = (owner, state) => {
    const selector = host.stateSelectors?.[state];
    return !!selector && owner.matches(selector);
  };

  const parseColor = (color) => {
    const match = color.match(/^rgba?\(\s*([\d.]+)[,\s]+([\d.]+)[,\s]+([\d.]+)(?:\s*[,/]\s*([\d.]+)%?)?\s*\)/i);
    if (match) {
      const alphaText = match[4];
      const alpha = alphaText === undefined ? 1 : Number(alphaText) * (color.includes(`${alphaText}%`) ? 0.01 : 1);
      return [Number(match[1]), Number(match[2]), Number(match[3]), alpha];
    }
    const canvas = host.document.createElement("canvas");
    canvas.width = 1;
    canvas.height = 1;
    const context = canvas.getContext("2d", { willReadFrequently: true });
    if (!context) return null;
    context.clearRect(0, 0, 1, 1);
    context.fillStyle = color;
    context.fillRect(0, 0, 1, 1);
    const pixel = context.getImageData(0, 0, 1, 1).data;
    return [pixel[0], pixel[1], pixel[2], pixel[3] / 255];
  };

  const nativeBackground = (variable) => {
    if (!variable) return null;
    if (host.nativeBackgroundCache.has(variable)) return host.nativeBackgroundCache.get(variable);
    const probe = host.document.createElement("div");
    probe.setAttribute("data-skin-manager-owned", "true");
    probe.style.position = "fixed";
    probe.style.visibility = "hidden";
    probe.style.backgroundColor = `var(${variable})`;
    host.document.body.appendChild(probe);
    const color = parseColor(host.getComputedStyle(probe).backgroundColor);
    probe.remove();
    host.nativeBackgroundCache.set(variable, color);
    return color;
  };

  const restoreFill = (surface) => {
    const applied = host.fillApplied.get(surface);
    if (!applied) return;
    const original = host.fillOriginals.get(surface);
    if (sameInlineFill(applied, readInlineFill(surface))) {
      if (original.value) surface.style.setProperty("background-color", original.value, original.priority);
      else surface.style.removeProperty("background-color");
    }
    host.fillOriginals.delete(surface);
    host.fillApplied.delete(surface);
  };

  const isAppliedFill = (element) => {
    const applied = host.fillApplied.get(element);
    return sameInlineFill(applied, readInlineFill(element))
      && applied.styleText === element.getAttribute("style");
  };

  const applyFill = (surface, owner, config) => {
    restoreFill(surface);
    const backgroundState = currentState(owner, config.backgroundStates);
    if (backgroundState ? config.noFillStates?.[backgroundState] : config.noFill) return;
    const state = currentState(owner, config.states);
    const opacity = state ? config.states[state] : config.default;
    if (opacity === null || opacity === undefined) return;
    let color = parseColor(host.getComputedStyle(surface).backgroundColor);
    if (color?.[3] === 0 && host.isBackgroundActive()
      && config.hasNativeMaterialBackground && !config.hasExplicitBackground && !config.hasExplicitColorToken) {
      color = nativeBackground(config.nativeBackgroundVariable);
    }
    if (!color || color[3] === 0) return;
    host.fillOriginals.set(surface, readInlineFill(surface));
    surface.style.setProperty("background-color", `rgba(${color[0]}, ${color[1]}, ${color[2]}, ${opacity})`, "important");
    host.fillApplied.set(surface, { ...readInlineFill(surface), styleText: surface.getAttribute("style") });
  };

  return { applyFill, currentState, isAppliedFill, matchesState, nativeBackground, parseColor, readInlineFill, restoreFill, sameInlineFill };
};

const createSkinFillBindings = (host) => {
  const bindings = new Map();
  const owners = new Map();
  const nativeBackgroundCache = new Map();
  const fillEngine = createSkinFillEngine({
    document: host.document,
    getComputedStyle: host.getComputedStyle,
    statePriority: host.codex.statePriority,
    stateSelectors: host.codex.stateSelectors,
    isBackgroundActive: host.isBackgroundActive,
    fillOriginals: new WeakMap(),
    fillApplied: new WeakMap(),
    nativeBackgroundCache,
  });
  const fillEvents = ["pointerover", "pointerout", "pointerdown", "pointerup", "focus", "blur", "focusin", "focusout"];

  const scheduleOwnerFill = (owner) => {
    const binding = owners.get(owner);
    if (!binding || binding.frame !== null) return;
    binding.frame = requestAnimationFrame(() => {
      binding.frame = null;
      fillEngine.applyFill(binding.surface, owner, binding.config);
    });
  };

  const unbind = (owner) => {
    const binding = owners.get(owner);
    if (!binding) return;
    if (binding.handler) {
      for (const eventName of fillEvents) owner.removeEventListener(eventName, binding.handler, true);
    }
    if (binding.frame !== null) cancelAnimationFrame(binding.frame);
    fillEngine.restoreFill(binding.surface);
    bindings.delete(binding.surface);
    owners.delete(owner);
  };

  const bind = (owner, part, config) => {
    unbind(owner);
    if (!config) return;
    const surface = host.surfaceFor(owner, part);
    if (!surface) return;
    const previous = bindings.get(surface);
    if (previous) unbind(previous.owner);
    const binding = { owner, part, config, surface, handler: null, frame: null };
    owners.set(owner, binding);
    bindings.set(surface, binding);
    fillEngine.applyFill(surface, owner, config);
    if (host.watchStaticFills || [config.states, config.noFillStates, config.backgroundStates]
      .some((states) => states && Object.keys(states).length > 0)) {
      binding.handler = () => scheduleOwnerFill(owner);
      for (const eventName of fillEvents) owner.addEventListener(eventName, binding.handler, true);
    }
  };

  const reset = () => {
    for (const owner of owners.keys()) unbind(owner);
    nativeBackgroundCache.clear();
  };

  return { bind, unbind, reset, bindings, scheduleOwnerFill, isAppliedFill: fillEngine.isAppliedFill };
};

const createSkinBackground = (host) => {
  const { document, getComputedStyle, codex } = host;
  const activeAttribute = "data-skin-background-active";
  let originalActive = null;
  let background = host.id ? document.getElementById(host.id) : null;

  const sync = (config) => {
    const root = document.documentElement;
    originalActive ??= { value: root.getAttribute(activeAttribute) };
    if (!config) {
      background?.remove();
      background = null;
      root.removeAttribute(activeAttribute);
      return;
    }
    if (!background) {
      background = document.createElement("div");
      if (host.id) background.id = host.id;
      if (host.className) background.className = host.className;
      Object.assign(background.style, {
        position: "fixed", inset: "0", zIndex: "0", pointerEvents: "none", overflow: "hidden",
      });
    }
    background.setAttribute("data-skin-manager-owned", "true");
    background.setAttribute("aria-hidden", "true");
    const shell = document.querySelector(codex.partSelectors.window);
    if (shell?.parentElement === document.body) {
      if (background.parentElement !== document.body || background.nextSibling !== shell) {
        document.body.insertBefore(background, shell);
      }
    } else if (background.parentElement !== document.body) {
      document.body.prepend(background);
    }
    root.setAttribute(activeAttribute, "true");
    const computedRoot = getComputedStyle(root);
    const fallback = codex.nativeBackgroundFallbackVariables.map((variable) => computedRoot.getPropertyValue(variable).trim()).find(Boolean)
      || getComputedStyle(document.body).backgroundColor;
    background.style.backgroundColor = config.color || fallback;

    let image = background.querySelector(".skin-manager-image");
    let overlay = background.querySelector(".skin-manager-overlay");
    const imageUrl = config.imageUrl ? host.resolveImageUrl(config.imageUrl) : "";
    if (imageUrl) {
      if (!image) {
        image = document.createElement("div");
        image.className = "skin-manager-image";
        image.setAttribute("data-skin-manager-owned", "true");
        Object.assign(image.style, { position: "absolute", inset: "0", backgroundRepeat: "no-repeat" });
        background.insertBefore(image, overlay);
      }
      const imageBackground = `url(${JSON.stringify(imageUrl)})`;
      if (image.style.backgroundImage !== imageBackground) image.style.backgroundImage = imageBackground;
      image.style.backgroundSize = config.fit || "";
      image.style.backgroundPosition = config.focusX != null && config.focusY != null
        ? `${config.focusX * 100}% ${config.focusY * 100}%`
        : config.focusX != null ? `${config.focusX * 100}%`
        : config.focusY != null ? `center ${config.focusY * 100}%` : "";
      image.style.opacity = config.opacity ?? "";
    } else {
      image?.remove();
    }

    if (config.overlayColor) {
      if (!overlay) {
        overlay = document.createElement("div");
        overlay.className = "skin-manager-overlay";
        overlay.setAttribute("data-skin-manager-owned", "true");
        Object.assign(overlay.style, { position: "absolute", inset: "0" });
        background.appendChild(overlay);
      }
      overlay.style.backgroundColor = config.overlayColor;
      overlay.style.opacity = config.overlayOpacity ?? "";
    } else {
      overlay?.remove();
    }
  };

  const remove = () => {
    background?.remove();
    background = null;
    if (!originalActive) return;
    const root = document.documentElement;
    if (originalActive.value === null) root?.removeAttribute(activeAttribute);
    else root?.setAttribute(activeAttribute, originalActive.value);
    originalActive = null;
  };

  return { sync, remove, isActive: () => document.documentElement?.getAttribute(activeAttribute) === "true" };
};
