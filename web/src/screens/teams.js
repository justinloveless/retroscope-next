export function mount(root, boot) {
  root.replaceChildren(document.createTextNode('teams'));
  return {
    update(params) {},
    unmount() {
      root.replaceChildren();
    },
  };
}
