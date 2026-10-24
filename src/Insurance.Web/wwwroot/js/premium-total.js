// The live sum of the premiums (new policy) or of the premium differences (change) of the selected coverages, as the
// legacy forms recalculated it on every input (policy-form.tsx, endorse-form.tsx). Display only: the server checks
// every amount again.
(function () {
  for (const form of document.querySelectorAll('form[data-premium-total]')) {
    const field = form.dataset.premiumTotal;
    const suffix = form.dataset.totalSuffix || '';
    const total = form.querySelector('[data-total]');
    const strong = total.querySelector('strong');
    form.addEventListener('input', () => {
      let sum = 0;
      for (const box of form.querySelectorAll('input[type=checkbox][data-code]')) {
        if (box.dataset.mandatory !== '1' && !box.checked) continue;
        const input = form.querySelector(`[name="cov_${box.dataset.code}_${field}"]`);
        sum += Number(String(input ? input.value : '').replace(',', '.')) || 0;
      }
      strong.textContent = sum.toFixed(2) + suffix;
      total.hidden = false;
    });
  }
})();
