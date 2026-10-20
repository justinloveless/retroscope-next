export function mount(root, boot) {
  root.replaceChildren(document.createTextNode('account'));
  return {
    update(params) {},
    unmount() {
      root.replaceChildren();
    },
  };
}
