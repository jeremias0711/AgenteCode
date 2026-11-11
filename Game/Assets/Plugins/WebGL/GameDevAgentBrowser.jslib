// GameDevAgent - bridge Web template -> Unity (read only, polled by MobileHudAdapter).
// The WebGL template (WebGLTemplates/GameDevAgent16x9/index.html) publishes the real visible layout of the page in
//   window.GDA_layoutJson / window.GDA_layoutVersion
// and Unity reads it from here. Polling avoids SendMessage calls to objects that may not exist yet (no console errors).
// Only used by the WebGL player; the Editor and the Windows build never see this file.
mergeInto(LibraryManager.library, {
  GDA_GetLayoutVersion: function () {
    return (typeof window !== 'undefined' && window.GDA_layoutVersion) ? (window.GDA_layoutVersion | 0) : 0;
  },
  // Mobile decision made by the template in <head> (touch + pointer:coarse, or ?gdaMobile=1/0).
  // 1 = mobile, 0 = desktop, -1 = unknown (template without the flag).
  GDA_GetIsMobile: function () {
    if (typeof window === 'undefined' || typeof window.GDA_isMobile !== 'boolean') return -1;
    return window.GDA_isMobile ? 1 : 0;
  },
  GDA_GetLayoutJson: function () {
    var s = (typeof window !== 'undefined' && window.GDA_layoutJson) ? String(window.GDA_layoutJson) : '';
    var size = lengthBytesUTF8(s) + 1;
    var buffer = _malloc(size);
    stringToUTF8(s, buffer, size);
    return buffer;
  }
});
