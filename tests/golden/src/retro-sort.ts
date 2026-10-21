// Verbatim spans of the original app; make-golden.mjs checks each one against its recorded sha256.

// golden-span src/components/retro/RetroColumn.tsx 50-64 7f48a79 sha256:ae5f50a41c552d8b3cc456637f62083dcb2ad5886c26485ccfb1c5d4b7fe7a91
export type SortKey = 'votes-desc' | 'votes-asc' | 'time-asc' | 'time-desc' | 'author-asc' | 'author-desc';

export const sortItems = (items: RetroItem[], sortKey: SortKey): RetroItem[] => {
  return [...items].sort((a, b) => {
    switch (sortKey) {
      case 'votes-desc': return b.votes !== a.votes ? b.votes - a.votes : new Date(a.created_at).getTime() - new Date(b.created_at).getTime();
      case 'votes-asc': return a.votes !== b.votes ? a.votes - b.votes : new Date(a.created_at).getTime() - new Date(b.created_at).getTime();
      case 'time-asc': return new Date(a.created_at).getTime() - new Date(b.created_at).getTime();
      case 'time-desc': return new Date(b.created_at).getTime() - new Date(a.created_at).getTime();
      case 'author-asc': return (a.author || '').localeCompare(b.author || '');
      case 'author-desc': return (b.author || '').localeCompare(a.author || '');
      default: return 0;
    }
  });
};
// golden-span-end
