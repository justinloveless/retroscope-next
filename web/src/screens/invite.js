export function mount(root, boot) {
  root.replaceChildren(document.createTextNode('invite'));
  return {
    update(params) {},
    unmount() {
      root.replaceChildren();
    },
  };
}
