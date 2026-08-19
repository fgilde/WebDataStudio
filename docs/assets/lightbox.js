// Opens the screenshots full size, with keyboard and arrows to walk through them. Plain DOM: the
// site has no build step, and a gallery is not worth one.
(function () {
  "use strict";

  var figures = Array.prototype.slice.call(document.querySelectorAll(".shots figure"));
  if (figures.length === 0) return;

  var shots = figures.map(function (figure) {
    var image = figure.querySelector("img");
    var caption = figure.querySelector("figcaption");
    return { src: image.getAttribute("src"), alt: image.getAttribute("alt") || "",
             caption: caption ? caption.textContent : "" };
  });

  var index = 0;

  var overlay = document.createElement("div");
  overlay.className = "lightbox";
  overlay.setAttribute("role", "dialog");
  overlay.setAttribute("aria-modal", "true");
  overlay.hidden = true;
  overlay.innerHTML =
    '<button class="lightbox-close" aria-label="Close">&times;</button>' +
    '<button class="lightbox-nav lightbox-prev" aria-label="Previous">&#8249;</button>' +
    '<figure class="lightbox-figure">' +
    '  <img alt="">' +
    '  <figcaption></figcaption>' +
    "</figure>" +
    '<button class="lightbox-nav lightbox-next" aria-label="Next">&#8250;</button>';

  document.body.appendChild(overlay);

  var image = overlay.querySelector("img");
  var caption = overlay.querySelector("figcaption");
  var opener = null;

  function show(next) {
    index = (next + shots.length) % shots.length;
    image.setAttribute("src", shots[index].src);
    image.setAttribute("alt", shots[index].alt);
    caption.textContent = shots[index].caption +
      " · " + (index + 1) + " of " + shots.length;
  }

  function open(at, from) {
    opener = from || null;
    show(at);
    overlay.hidden = false;
    document.body.style.overflow = "hidden";
    overlay.querySelector(".lightbox-close").focus();
  }

  function close() {
    overlay.hidden = true;
    document.body.style.overflow = "";
    // Back to the thumbnail that opened it, so the keyboard does not lose its place.
    if (opener) opener.focus();
  }

  figures.forEach(function (figure, at) {
    var button = figure.querySelector("img");
    button.setAttribute("tabindex", "0");
    button.setAttribute("role", "button");
    button.setAttribute("title", "Show full size");
    figure.classList.add("zoomable");

    button.addEventListener("click", function () { open(at, button); });
    button.addEventListener("keydown", function (event) {
      if (event.key === "Enter" || event.key === " ") {
        event.preventDefault();
        open(at, button);
      }
    });
  });

  overlay.querySelector(".lightbox-close").addEventListener("click", close);
  overlay.querySelector(".lightbox-prev").addEventListener("click", function () { show(index - 1); });
  overlay.querySelector(".lightbox-next").addEventListener("click", function () { show(index + 1); });

  // A click on the backdrop closes; a click on the picture itself must not.
  overlay.addEventListener("click", function (event) {
    if (event.target === overlay) close();
  });

  document.addEventListener("keydown", function (event) {
    if (overlay.hidden) return;
    if (event.key === "Escape") close();
    if (event.key === "ArrowLeft") show(index - 1);
    if (event.key === "ArrowRight") show(index + 1);
  });
})();
