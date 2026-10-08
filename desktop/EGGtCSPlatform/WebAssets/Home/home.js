const send = value => window.invokeCSharpAction?.(JSON.stringify(value));
document.querySelectorAll('[data-action]').forEach(button => {
  button.addEventListener('click', () => {
    if (!button.disabled) send({type:'action', action:button.dataset.action});
  });
});
let acknowledged = false;
window.updateHome = state => {
  acknowledged = true;
  for (const item of state.cards) {
    const button = document.querySelector('[data-action="' + item.id + '"]');
    if (!button) continue;
    button.disabled = !item.enabled;
    button.title = item.reason || '';
    const text = button.querySelector('small');
    text.replaceChildren();
    text.classList.toggle('is-success', !!item.connected);
    if (item.connected) {
      const icon = document.createElement('img');
      icon.src = 'home-device-connected.svg'; icon.className = 'home-device-connected-icon'; icon.alt = '';
      text.append(icon);
    }
    text.append(document.createTextNode(item.subtitle));
  }
};
const handshake = setInterval(() => {
  if (acknowledged) clearInterval(handshake);
  else send({type:'ready'});
}, 150);
