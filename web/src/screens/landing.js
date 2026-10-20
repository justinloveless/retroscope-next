export function mount(root, boot) {
  root.replaceChildren(document.createTextNode('landing'));
  return {
    update(params) {},
    unmount() {
      root.replaceChildren();
    },
  };
}
