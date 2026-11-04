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
    const original = host.fillOriginals.get(surface);
    if (!original) return;
    const applied = host.fillApplied.get(surface);
    const current = readInlineFill(surface);
    if (applied && !sameInlineFill(applied, current)) host.fillOriginals.set(surface, current);
    const saved = host.fillOriginals.get(surface);
    if (saved.value) surface.style.setProperty("background-color", saved.value, saved.priority);
    else surface.style.removeProperty("background-color");
    host.fillApplied.delete(surface);
  };

  const isAppliedFill = (element) => {
    const applied = host.fillApplied.get(element);
    return sameInlineFill(applied, readInlineFill(element))
      && applied.styleText === element.getAttribute("style");
  };

  const applyFill = (surface, owner, config) => {
    if (!host.fillOriginals.has(surface)) host.fillOriginals.set(surface, readInlineFill(surface));
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
    surface.style.setProperty("background-color", `rgba(${color[0]}, ${color[1]}, ${color[2]}, ${opacity})`, "important");
    host.fillApplied.set(surface, { ...readInlineFill(surface), styleText: surface.getAttribute("style") });
  };

  return { applyFill, currentState, isAppliedFill, matchesState, nativeBackground, parseColor, readInlineFill, restoreFill, sameInlineFill };
};
