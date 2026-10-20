export function mount(root, boot) {
  root.replaceChildren(document.createTextNode('retro'));
  return {
    update(params) {},
    unmount() {
      root.replaceChildren();
    },
  };
}
