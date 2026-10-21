export function mount(root, boot) {
  root.replaceChildren(document.createTextNode('dashboard'));
  return {
    update(params) {},
    unmount() {
      root.replaceChildren();
    },
  };
}
