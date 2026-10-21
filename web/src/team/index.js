export function mount(root, boot) {
  root.replaceChildren(document.createTextNode('team'));
  return {
    update(params) {},
    unmount() {
      root.replaceChildren();
    },
  };
}
