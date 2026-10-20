export function mount(root, boot) {
  root.replaceChildren(document.createTextNode('mentions'));
  return {
    update(params) {},
    unmount() {
      root.replaceChildren();
    },
  };
}
