export function mount(root, boot) {
  root.replaceChildren(document.createTextNode('auth'));
  return {
    update(params) {},
    unmount() {
      root.replaceChildren();
    },
  };
}
