// Animates advanced <details> groups while keeping their native open state synchronized.
document.addEventListener('DOMContentLoaded', () => {
    const accordions = document.querySelectorAll('details.advanced-group');

    accordions.forEach(details => {
        const summary = details.querySelector('summary');
        const content = details.querySelector('.group-content');
        let animation = null;
        let isClosing = false;
        let isExpanding = false;

        summary.addEventListener('click', (e) => {
            // Keep the native toggle under animation control.
            e.preventDefault();
            details.style.overflow = 'hidden';

            if (isClosing || !details.open) {
                open();
            } else if (isExpanding || details.open) {
                shrink();
            }
        });

        function shrink() {
            isClosing = true;
            const startHeight = `${details.offsetHeight}px`;
            const endHeight = `${summary.offsetHeight}px`;

            if (animation) animation.cancel();

            animation = details.animate({
                height: [startHeight, endHeight]
            }, {
                duration: 250,
                easing: 'cubic-bezier(0.4, 0, 0.2, 1)'
            });

            animation.onfinish = () => {
                details.open = false;
                onAnimationFinish();
            };
            animation.oncancel = () => isClosing = false;
        }

        function open() {
            details.style.height = `${details.offsetHeight}px`;
            details.open = true;
            window.requestAnimationFrame(() => {
                isExpanding = true;
                const startHeight = `${details.offsetHeight}px`;
                const endHeight = `${summary.offsetHeight + content.offsetHeight}px`;

                if (animation) animation.cancel();

                animation = details.animate({
                    height: [startHeight, endHeight]
                }, {
                    duration: 250, 
                    easing: 'cubic-bezier(0.4, 0, 0.2, 1)'
                });

                animation.onfinish = () => onAnimationFinish();
                animation.oncancel = () => isExpanding = false;
            });
        }

        function onAnimationFinish() {
            animation = null;
            isClosing = false;
            isExpanding = false;
            details.style.height = details.style.overflow = '';
        }
    });
});