(() => {
  let count = 0;
  const timer = setInterval(() => {
    if (Array.isArray(window.GLOBALS) && typeof window.GLOBALS[9] === 'string') {
      document.documentElement.dataset.localSmtpIk = window.GLOBALS[9];
      clearInterval(timer);
    } else if (++count >= 40) clearInterval(timer);
  }, 250);
})();
