export function mount(root, boot) {
  root.replaceChildren(document.createTextNode('admin'));
  return {
    update(params) {},
    unmount() {
      root.replaceChildren();
    },
  };
}
